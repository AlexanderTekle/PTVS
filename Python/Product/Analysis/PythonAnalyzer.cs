﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.PythonTools.Analysis.Interpreter;
using Microsoft.PythonTools.Analysis.Values;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;
using Microsoft.PythonTools.PyAnalysis;

namespace Microsoft.PythonTools.Analysis {
    /// <summary>
    /// Performs analysis of multiple Python code files and enables interrogation of the resulting analysis.
    /// </summary>
    public class PythonAnalyzer : IGroupableAnalysisProject, IDisposable {
        private readonly IPythonInterpreter _interpreter;
        private readonly ModuleTable _modules;
        private readonly ConcurrentDictionary<string, ModuleInfo> _modulesByFilename;
        private readonly Dictionary<object, Namespace> _itemCache;
        private readonly string _builtinName;
        internal BuiltinModule _builtinModule;
        private readonly ConcurrentDictionary<string, XamlProjectEntry> _xamlByFilename = new ConcurrentDictionary<string, XamlProjectEntry>();
        internal ConstantInfo _noneInst;
        private readonly Deque<AnalysisUnit> _queue;
        private Action<int> _reportQueueSize;
        private int _reportQueueInterval;
        internal readonly IModuleContext _defaultContext;
        private readonly PythonLanguageVersion _langVersion;
        internal readonly AnalysisUnit _evalUnit;   // a unit used for evaluating when we don't otherwise have a unit available
        private readonly HashSet<string> _analysisDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<SpecializationInfo>> _specializationInfo = new Dictionary<string, List<SpecializationInfo>>();  // delayed specialization information, for modules not yet loaded...
        private AnalysisLimits _limits = new AnalysisLimits();
        private static object _nullKey = new object();

        public PythonAnalyzer(IPythonInterpreterFactory interpreterFactory)
            : this(interpreterFactory.CreateInterpreter(), interpreterFactory.GetLanguageVersion()) {
        }

        public PythonAnalyzer(IPythonInterpreter pythonInterpreter, PythonLanguageVersion langVersion)
            : this(pythonInterpreter, langVersion, langVersion.Is3x() ? SharedDatabaseState.BuiltinName3x : SharedDatabaseState.BuiltinName2x) {
        }

        internal PythonAnalyzer(IPythonInterpreter pythonInterpreter, PythonLanguageVersion langVersion, string builtinName) {
            _langVersion = langVersion;
            _interpreter = pythonInterpreter;
            _builtinName = builtinName;
            _modules = new ModuleTable(this, _interpreter, _interpreter.GetModuleNames());
            _modulesByFilename = new ConcurrentDictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
            _itemCache = new Dictionary<object, Namespace>();

            _queue = new Deque<AnalysisUnit>();

            LoadKnownTypes();

            pythonInterpreter.Initialize(this);

            _defaultContext = _interpreter.CreateModuleContext();

            _evalUnit = new AnalysisUnit(null, null, new ModuleInfo("$global", new ProjectEntry(this, "$global", String.Empty, null), _defaultContext).Scope, true);
            AnalysisLog.NewUnit(_evalUnit);
        }

        private void LoadKnownTypes() {
            _builtinModule = (BuiltinModule)Modules[_builtinName].Module;

            Types = new KnownTypes(this);
            ClassInfos = (IKnownClasses)Types;

            _noneInst = (ConstantInfo)GetNamespaceFromObjects(null);

            SpecializeFunction(_builtinName, "range", (n, unit, args, argNames) => unit.Scope.GetOrMakeNodeValue(n, (nn) => new RangeInfo(Types[BuiltinTypeId.List], unit.ProjectState).SelfSet), analyze: false);
            SpecializeFunction(_builtinName, "min", ReturnUnionOfInputs);
            SpecializeFunction(_builtinName, "max", ReturnUnionOfInputs);
            SpecializeFunction(_builtinName, "getattr", SpecialGetAttr, analyze: false);
            SpecializeFunction(_builtinName, "next", SpecialNext, analyze: false);
            SpecializeFunction(_builtinName, "iter", SpecialIter, analyze: false);
            SpecializeFunction(_builtinName, "super", SpecialSuper, analyze: false);

            // analyzing the copy module causes an explosion in types (it gets called w/ all sorts of types to be
            // copied, and always returns the same type).  So we specialize these away so they return the type passed
            // in and don't do any analyze.  Ditto for the rest of the functions here...  
            SpecializeFunction("copy", "deepcopy", CopyFunction, analyze: false);
            SpecializeFunction("copy", "copy", CopyFunction, analyze: false);
            SpecializeFunction("pickle", "dumps", ReturnsBytes, analyze: false);
            SpecializeFunction("UserDict.UserDict", "update", Nop, analyze: false);
            SpecializeFunction("pprint", "pprint", Nop, analyze: false);
            SpecializeFunction("pprint", "pformat", ReturnsString, analyze: false);
            SpecializeFunction("pprint", "saferepr", ReturnsString, analyze: false);
            SpecializeFunction("pprint", "_safe_repr", ReturnsString, analyze: false);
            SpecializeFunction("pprint", "_format", ReturnsString, analyze: false);
            SpecializeFunction("pprint.PrettyPrinter", "_format", ReturnsString, analyze: false);
            SpecializeFunction("decimal.Decimal", "__new__", Nop, analyze: false);
            SpecializeFunction("StringIO.StringIO", "write", Nop, analyze: false);
            SpecializeFunction("threading.Thread", "__init__", Nop, analyze: false);
            SpecializeFunction("subprocess.Popen", "__init__", Nop, analyze: false);
            SpecializeFunction("Tkinter.Toplevel", "__init__", Nop, analyze: false);
            SpecializeFunction("weakref.WeakValueDictionary", "update", Nop, analyze: false);
            SpecializeFunction("os._Environ", "get", ReturnsString, analyze: false);
            SpecializeFunction("os._Environ", "update", Nop, analyze: false);
            SpecializeFunction("ntpath", "expandvars", ReturnsString, analyze: false);
            SpecializeFunction("idlelib.EditorWindow.EditorWindow", "__init__", Nop, analyze: false);

            // cached for quick checks to see if we're a call to clr.AddReference

            SpecializeFunction("wpf", "LoadComponent", LoadComponent);
        }

        /// <summary>
        /// Reloads the modules from the interpreter.
        /// 
        /// This method should be called on the analysis thread and is usually invoked
        /// when the interpreter signals that it's modules have changed.
        /// </summary>
        public void ReloadModules() {
            _modules.ReInit();
            LoadKnownTypes();

            _interpreter.Initialize(this);

            foreach (var mod in _modulesByFilename.Values) {
                mod.Clear();
            }
        }

        #region Public API

        public PythonLanguageVersion LanguageVersion {
            get {
                return _langVersion;
            }
        }

        /// <summary>
        /// Adds a new module of code to the list of available modules and returns a ProjectEntry object.
        /// 
        /// This method is thread safe.
        /// </summary>
        /// <param name="moduleName">The name of the module; used to associate with imports</param>
        /// <param name="filePath">The path to the file on disk</param>
        /// <param name="cookie">An application-specific identifier for the module</param>
        /// <returns></returns>
        public IPythonProjectEntry AddModule(string moduleName, string filePath, IAnalysisCookie cookie = null) {
            var entry = new ProjectEntry(this, moduleName, filePath, cookie);

            if (moduleName != null) {
                Modules[moduleName] = new ModuleReference(entry.MyScope);

                DoDelayedSpecialization(moduleName);
            }
            if (filePath != null) {
                _modulesByFilename[filePath] = entry.MyScope;
            }
            return entry;
        }

        /// <summary>
        /// Removes the specified project entry from the current analysis.
        /// 
        /// This method is thread safe.
        /// </summary>
        public void RemoveModule(IProjectEntry entry) {
            if (entry == null) {
                throw new ArgumentNullException("entry");
            }
            Contract.EndContractBlock();

            ModuleInfo removed;
            _modulesByFilename.TryRemove(entry.FilePath, out removed);

            var pyEntry = entry as IPythonProjectEntry;
            if (pyEntry != null) {
                ModuleReference modRef;
                Modules.TryRemove(pyEntry.ModuleName, out modRef);
                var projEntry2 = entry as IProjectEntry2;
                if (projEntry2 != null) {
                    projEntry2.RemovedFromProject();
                }
            }
        }

        /// <summary>
        /// Adds a XAML file to be analyzed.  
        /// 
        /// This method is thread safe.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="cookie"></param>
        /// <returns></returns>
        public IXamlProjectEntry AddXamlFile(string filePath, IAnalysisCookie cookie = null) {
            var entry = new XamlProjectEntry(filePath);

            _xamlByFilename[filePath] = entry;

            return entry;
        }

        /// <summary>
        /// Looks up the specified module by name.
        /// </summary>
        public MemberResult[] GetModule(string name) {
            return GetModules(modName => modName != name);
        }

        /// <summary>
        /// Gets a top-level list of all the available modules as a list of MemberResults.
        /// </summary>
        /// <returns></returns>
        public MemberResult[] GetModules(bool topLevelOnly = false) {
            return GetModules(modName => topLevelOnly && modName.IndexOf('.') != -1);
        }

        private MemberResult[] GetModules(Func<string, bool> excludedPredicate) {
            var d = new Dictionary<string, List<ModuleLoadState>>();
            foreach (var keyValue in Modules) {
                var modName = keyValue.Key;
                var moduleRef = keyValue.Value;

                if (String.IsNullOrWhiteSpace(modName) ||
                    excludedPredicate(modName)) {
                    continue;
                }

                if (moduleRef.IsValid) {
                    List<ModuleLoadState> l;
                    if (!d.TryGetValue(modName, out l)) {
                        d[modName] = l = new List<ModuleLoadState>();
                    }
                    if (moduleRef.HasModule) {
                        // The REPL shows up here with value=None
                        l.Add(moduleRef);
                    }
                }
            }

            return ModuleDictToMemberResult(d);
        }

        private static MemberResult[] ModuleDictToMemberResult(Dictionary<string, List<ModuleLoadState>> d) {
            var result = new MemberResult[d.Count];
            int pos = 0;
            foreach (var kvp in d) {
                var lazyEnumerator = new LazyModuleEnumerator(kvp.Value);
                result[pos++] = new MemberResult(
                    kvp.Key,
                    lazyEnumerator.GetLazyModules,
                    lazyEnumerator.GetModuleType
                );
            }
            return result;
        }

        class LazyModuleEnumerator {
            private readonly List<ModuleLoadState> _loaded;

            public LazyModuleEnumerator(List<ModuleLoadState> loaded) {
                _loaded = loaded;
            }

            public IEnumerable<Namespace> GetLazyModules() {
                foreach (var value in _loaded) {
                    yield return value.Module;
                }
            }

            public PythonMemberType GetModuleType() {
                PythonMemberType? type = null;
                foreach (var value in _loaded) {
                    if (type == null) {
                        type = value.MemberType;
                    } else if (type != value.MemberType) {
                        type = PythonMemberType.Multiple;
                        break;
                    }
                }
                return type ?? PythonMemberType.Unknown;
            }
        }

        /// <summary>
        /// Searches all modules which match the given name and searches in the modules
        /// for top-level items which match the given name.  Returns a list of all the
        /// available names fully qualified to their name.  
        /// </summary>
        /// <param name="name"></param>
        public IEnumerable<ExportedMemberInfo> FindNameInAllModules(string name) {
            // provide module names first
            foreach (var keyValue in Modules) {
                var modName = keyValue.Key;
                var moduleRef = keyValue.Value;

                if (moduleRef.IsValid) {
                    // include modules which can be imported
                    if (modName == name || PackageNameMatches(name, modName)) {
                        yield return new ExportedMemberInfo(modName, true);
                    }
                }
            }

            // then include module members
            foreach (var keyValue in Modules) {
                var modName = keyValue.Key;
                var moduleRef = keyValue.Value;

                if (moduleRef.IsValid) {
                    // then check for members within the module.
                    if (moduleRef.ModuleContainsMember(_defaultContext, name)) {
                        yield return new ExportedMemberInfo(modName + "." + name, true);
                    } else {
                        yield return new ExportedMemberInfo(modName + "." + name, false);
                    }
                }
            }
        }

        private static bool PackageNameMatches(string name, string modName) {
            int lastDot;
            return (lastDot = modName.LastIndexOf('.')) != -1 &&
                modName.Length == lastDot + 1 + name.Length &&
                String.Compare(modName, lastDot + 1, name, 0, name.Length) == 0;
        }

        /// <summary>
        /// Returns the interpreter that the analyzer is using.
        /// 
        /// This property is thread safe.
        /// </summary>
        public IPythonInterpreter Interpreter {
            get {
                return _interpreter;
            }
        }

        /// <summary>
        /// returns the MemberResults associated with modules in the specified
        /// list of names.  The list of names is the path through the module, for example
        /// ['System', 'Runtime']
        /// </summary>
        /// <returns></returns>
        public MemberResult[] GetModuleMembers(IModuleContext moduleContext, string[] names, bool includeMembers = false) {
            ModuleReference moduleRef;
            if (Modules.TryGetValue(names[0], out moduleRef) && moduleRef.Module != null) {
                var module = moduleRef.Module as IModule;
                if (module != null) {
                    return GetModuleMembers(moduleContext, names, includeMembers, module);
                }

            }

            return new MemberResult[0];
        }

        internal static MemberResult[] GetModuleMembers(IModuleContext moduleContext, string[] names, bool includeMembers, IModule module) {
            for (int i = 1; i < names.Length && module != null; i++) {
                module = module.GetChildPackage(moduleContext, names[i]);
            }

            if (module != null) {
                List<MemberResult> result = new List<MemberResult>();
                if (includeMembers) {
                    foreach (var keyValue in module.GetAllMembers(moduleContext)) {
                        result.Add(new MemberResult(keyValue.Key, keyValue.Value));
                    }
                    return result.ToArray();
                } else {
                    foreach (var child in module.GetChildrenPackages(moduleContext)) {
                        result.Add(new MemberResult(child.Key, child.Key, new[] { child.Value }, PythonMemberType.Module));
                    }
                    foreach (var keyValue in module.GetAllMembers(moduleContext)) {
                        bool anyModules = false;
                        foreach(var ns in keyValue.Value.OfType<MultipleMemberInfo>()) {
                            if (ns.Members.OfType<IModule>().Any(mod => !(mod is MultipleMemberInfo))) {
                                anyModules = true;
                                break;
                            }
                        }
                        if (anyModules) {
                            result.Add(new MemberResult(keyValue.Key, keyValue.Value));
                        }
                    }
                    return result.ToArray();
                }
            }
            return new MemberResult[0];
        }

        /// <summary>
        /// Specializes the provided function in the given module name to return an instance of the given type.
        /// 
        /// The type is a fully qualified module name (e.g. thread.LockType).  
        /// </summary>
        /// <param name="moduleName"></param>
        /// <param name="name"></param>
        /// <param name="returnType"></param>
        public void SpecializeFunction(string moduleName, string name, string returnType) {
            int lastDot;
            if ((lastDot = returnType.LastIndexOf('.')) == -1) {
                throw new ArgumentException(String.Format("Expected module.typename for return type, got '{0}'", returnType));
            }

            string retModule = returnType.Substring(0, lastDot);
            string typeName = returnType.Substring(lastDot + 1);

            SpecializeFunction(moduleName, name, (call, unit, types, argNames) => {
                ModuleReference modRef;
                if (Modules.TryGetValue(retModule, out modRef)) {
                    if (modRef.Module != null) {
                        var res = NamespaceSet.Empty;
                        foreach (var value in modRef.Namespace.GetMember(call, unit, typeName)) {
                            if (value is ClassInfo) {
                                res = res.Union(((ClassInfo)value).Instance.SelfSet);
                            } else {
                                res = res.Union(value.SelfSet);
                            }
                        }
                        return res;
                    }
                }
                return null;
            });
        }

        /// <summary>
        /// Enables specializaing a call.
        /// 
        /// New in 1.5.
        /// </summary>
        public void SpecializeFunction(string moduleName, string name, Func<CallExpression, CallInfo, IEnumerable<AnalysisValue>> dlg) {
            SpecializeFunction(moduleName, name, (call, unit, types, argNames) => {
                var res = dlg(call, new CallInfo(types, argNames));
                if (res != null) {
                    var set = NamespaceSet.Empty;
                    foreach (var obj in res) {
                        set = set.Union(obj.AsNamespace());
                    }
                    return set;
                }
                return null;
            });
        }

        public void SpecializeFunction(string moduleName, string name, Action<CallExpression> dlg) {
            SpecializeFunction(moduleName, name, (call, unit, types, argNames) => { dlg(call); return null; });
        }

        public void SpecializeFunction(string moduleName, string name, Action<PythonAnalyzer, CallExpression> dlg) {
            SpecializeFunction(moduleName, name, (call, unit, types, argNames) => { dlg(this, call); return null; });
        }

        /// <summary>
        /// Gets the list of directories which should be analyzed.
        /// 
        /// This property is thread safe.
        /// </summary>
        public IEnumerable<string> AnalysisDirectories {
            get {
                lock (_analysisDirs) {
                    return _analysisDirs.ToArray();
                }
            }
        }

        public static string PathToModuleName(string path) {
            return PathToModuleName(path, fileName => File.Exists(fileName));
        }

        /// <summary>
        /// Converts a given absolute path name to a fully qualified Python module name by walking the directory tree.
        /// </summary>
        /// <param name="path">Path to convert.</param>
        /// <param name="fileExists">A function that is used to verify the existence of files (in particular, __init__.py)
        /// in the tree. Its signature and semantics should match that of <see cref="File.Exists"/>.</param>
        /// <returns>A fully qualified module name.</returns>
        public static string PathToModuleName(string path, Func<string, bool> fileExists) {
            string moduleName;
            string dirName;

            if (path == null) {
                return String.Empty;
            } else if (path.EndsWith("__init__.py")) {
                moduleName = Path.GetFileName(Path.GetDirectoryName(path));
                dirName = Path.GetDirectoryName(path);
            } else {
                moduleName = Path.GetFileNameWithoutExtension(path);
                dirName = path;
            }

            while (dirName.Length != 0 && (dirName = Path.GetDirectoryName(dirName)).Length != 0 &&
                fileExists(Path.Combine(dirName, "__init__.py"))) {
                moduleName = Path.GetFileName(dirName) + "." + moduleName;
            }

            return moduleName;
        }

        public AnalysisLimits Limits {
            get { return _limits; }
            set { _limits = value; }
        }

        #endregion

        #region Internal Implementation

        internal IKnownPythonTypes Types {
            get;
            private set;
            }

        internal IKnownClasses ClassInfos {
            get;
            private set;
        }

        /// <summary>
        /// Replaces a built-in function (specified by module name and function name) with a customized
        /// delegate which provides specific behavior for handling when that function is called.
        /// 
        /// Currently this just provides a hook when the function is called - it could be expanded
        /// to providing the interpretation of when the function is called as well.
        /// </summary>
        private void SpecializeFunction(string moduleName, string name, Func<CallExpression, AnalysisUnit, INamespaceSet[], NameExpression[], INamespaceSet> dlg, bool analyze = true, bool save = true) {
            ModuleReference module;

            int lastDot;
            string realModName = null;
            if (Modules.TryGetValue(moduleName, out module)) {
                IModule mod = module.Module as IModule;
                Debug.Assert(mod != null);
                if (mod != null) {
                    mod.SpecializeFunction(name, dlg, analyze);
                }
            } else if ((lastDot = moduleName.LastIndexOf('.')) != -1 &&
                Modules.TryGetValue(realModName = moduleName.Substring(0, lastDot), out module)) {

                IModule mod = module.Module as IModule;
                Debug.Assert(mod != null);
                if (mod != null) {
                    mod.SpecializeFunction(moduleName.Substring(lastDot + 1, moduleName.Length - (lastDot + 1)) + "." + name, dlg, analyze);
                }
            }

            if (save) {
                SaveDelayedSpecialization(moduleName, name, dlg, analyze, realModName);
            }
        }

        private INamespaceSet LoadComponent(CallExpression node, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            if (args.Length == 2 && Interpreter is IDotNetPythonInterpreter) {
                var xaml = args[1];
                var self = args[0];

                foreach (var arg in xaml) {
                    string strConst = arg.GetConstantValue() as string;
                    if (strConst == null) {
                        var bytes = arg.GetConstantValue() as AsciiString;
                        if (bytes != null) {
                            strConst = bytes.String;
                        }
                    }

                    if (strConst != null) {
                        // process xaml file, add attributes to self
                        string xamlPath = Path.Combine(Path.GetDirectoryName(unit.DeclaringModule.ProjectEntry.FilePath), strConst);
                        XamlProjectEntry xamlProject;
                        if (_xamlByFilename.TryGetValue(xamlPath, out xamlProject)) {
                            // TODO: Get existing analysis if it hasn't changed.
                            var analysis = xamlProject.Analysis;

                            if (analysis == null) {
                                xamlProject.Analyze(CancellationToken.None);
                                analysis = xamlProject.Analysis;
                            }

                            xamlProject.AddDependency(unit.ProjectEntry);

                            var evalUnit = unit.CopyForEval();

                            // add named objects to instance
                            foreach (var keyValue in analysis.NamedObjects) {
                                var type = keyValue.Value;
                                if (type.Type.UnderlyingType != null) {

                                    var ns = GetNamespaceFromObjects(((IDotNetPythonInterpreter)Interpreter).GetBuiltinType(type.Type.UnderlyingType));
                                    if (ns is BuiltinClassInfo) {
                                        ns = ((BuiltinClassInfo)ns).Instance;
                                    }
                                    self.SetMember(node, evalUnit, keyValue.Key, ns.SelfSet);
                                }

                                // TODO: Better would be if SetMember took something other than a node, then we'd
                                // track references w/o this extra effort.
                                foreach (var inst in self) {
                                    InstanceInfo instInfo = inst as InstanceInfo;
                                    if (instInfo != null && instInfo.InstanceAttributes != null) {
                                        VariableDef def;
                                        if (instInfo.InstanceAttributes.TryGetValue(keyValue.Key, out def)) {
                                            def.AddAssignment(
                                                new EncodedLocation(SourceLocationResolver.Instance, new SourceLocation(1, type.LineNumber, type.LineOffset)),
                                                xamlProject
                                            );
                                        }
                                    }
                                }
                            }

                            // add references to event handlers
                            foreach (var keyValue in analysis.EventHandlers) {
                                // add reference to methods...
                                var member = keyValue.Value;

                                // TODO: Better would be if SetMember took something other than a node, then we'd
                                // track references w/o this extra effort.
                                foreach (var inst in self) {
                                    InstanceInfo instInfo = inst as InstanceInfo;
                                    if (instInfo != null) {
                                        ClassInfo ci = instInfo.ClassInfo;

                                        VariableDef def;
                                        if (ci.Scope.Variables.TryGetValue(keyValue.Key, out def)) {
                                            def.AddReference(
                                                new EncodedLocation(SourceLocationResolver.Instance, new SourceLocation(1, member.LineNumber, member.LineOffset)),
                                                xamlProject
                                            );
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // load component returns self
                return self;
            }

            return NamespaceSet.Empty;
        }

        internal Deque<AnalysisUnit> Queue {
            get {
                return _queue;
            }
        }

        private IModule ImportFromIMember(IMember member, string[] names, int curIndex) {
            if (member == null) {
                return null;
            }
            if (curIndex >= names.Length) {
                return GetNamespaceFromObjects(member) as IModule;
            }

            var ipm = member as IPythonModule;
            if (ipm != null) {
                return ImportFromIPythonModule(ipm, names, curIndex);
            }

            var im = member as IModule;
            if (im != null) {
                return ImportFromIModule(im, names, curIndex);
            }

            var ipmm = member as IPythonMultipleMembers;
            if (ipmm != null) {
                return ImportFromIPythonMultipleMembers(ipmm, names, curIndex);
            }

            return null;
        }

        private IModule ImportFromIPythonMultipleMembers(IPythonMultipleMembers mod, string[] names, int curIndex) {
            if (mod == null) {
                return null;
            }
            var modules = new List<IModule>();
            foreach (var member in mod.Members) {
                modules.Add(ImportFromIMember(member, names, curIndex));
            }
            var mods = modules.OfType<Namespace>().ToArray();
            if (mods.Length == 0) {
                return null;
            } else if (mods.Length == 1) {
                return (IModule)mods[0];
            } else {
                return new MultipleMemberInfo(mods);
            }
        }

        private IModule ImportFromIModule(IModule mod, string[] names, int curIndex) {
            for (; mod != null && curIndex < names.Length; ++curIndex) {
                mod = mod.GetChildPackage(_defaultContext, names[curIndex]);
            }
            return mod;
        }

        private IModule ImportFromIPythonModule(IPythonModule mod, string[] names, int curIndex) {
            if (mod == null) {
                return null;
            }
            var member = mod.GetMember(_defaultContext, names[curIndex]);
            return ImportFromIMember(member, names, curIndex + 1);
        }

        internal IModule ImportBuiltinModule(string modName, bool bottom = true) {
            IPythonModule mod = null;

            if (modName.IndexOf('.') != -1) {
                string[] names = modName.Split('.');
                if (names[0].Length > 0) {
                    mod = _interpreter.ImportModule(names[0]);
                    if (bottom && names.Length > 1) {
                        var mod2 = ImportFromIPythonModule(mod, names, 1);
                        if (mod2 != null) {
                            return mod2;
                        }
                    }
                }
                // else relative import, we're not getting a builtin module...
            } else {
                mod = _interpreter.ImportModule(modName);
            }

            if (mod != null) {
                return (BuiltinModule)GetNamespaceFromObjects(mod);
            }

            return null;
        }

        private INamespaceSet Nop(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            return NamespaceSet.Empty;
        }

        private INamespaceSet CopyFunction(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            if (args.Length > 0) {
                return args[0];
            }
            return NamespaceSet.Empty;
        }

        private INamespaceSet ReturnsBytes(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            return ClassInfos[BuiltinTypeId.Bytes].Instance;
        }

        private INamespaceSet ReturnsString(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            return ClassInfos[BuiltinTypeId.Str].Instance;
        }

        private INamespaceSet SpecialGetAttr(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            var res = NamespaceSet.Empty;
            if (args.Length >= 2) {
                if (args.Length >= 3) {
                    // getattr(foo, 'bar', baz), baz is a possible return value.
                    res = args[2];
                }

                foreach (var value in args[0]) {
                    foreach (var name in args[1]) {
                        // getattr(foo, 'bar') - attempt to do the getattr and return the proper value
                        var strValue = name.GetConstantValueAsString();
                        if (strValue != null) {
                            res = res.Union(value.GetMember(call, unit, strValue));
                        }
                    }
                }
            }
            return res;
        }

        private INamespaceSet SpecialNext(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            var nextName = (unit.ProjectState.LanguageVersion.Is3x()) ? "__next__" : "next";
            var newArgs = args.Skip(1).ToArray();
            var newNames = argNames.Skip(1).ToArray();

            return args[0].GetMember(call, unit, nextName).Call(call, unit, newArgs, newNames);
        }

        private INamespaceSet SpecialIter(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            if (args.Length == 1) {
                return args[0].GetIterator(call, unit);
            } else if (args.Length == 2) {
                var iterator = unit.Scope.GetOrMakeNodeValue(call, n => {
                    var iterTypes = new[] { new VariableDef() };
                    return new IteratorInfo(iterTypes, ClassInfos[BuiltinTypeId.CallableIterator], call);
                });
                foreach (var iter in iterator.OfType<IteratorInfo>()) {
                    // call the callable object
                    // the sentinel's type is never seen, so don't include it
                    iter.AddTypes(unit, new[] { args[0].Call(call, unit, ExpressionEvaluator.EmptyNamespaces, ExpressionEvaluator.EmptyNames) });
                }
                return iterator;
            } else {
                return NamespaceSet.Empty;
            }
        }

        private INamespaceSet SpecialSuper(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            if (args.Length < 0 || args.Length > 2) {
                return NamespaceSet.Empty;
            }

            var classes = NamespaceSet.Empty;
            var instances = NamespaceSet.Empty;

            if (args.Length == 0) {
                if (unit.ProjectState.LanguageVersion.Is3x()) {
                    // No-arg version is magic in 3k - first arg is implicitly the enclosing class, and second is implicitly
                    // the first argument of the enclosing method. Look up that information from the scope.
                    // We want to find the nearest enclosing class scope, and the function scope that is immediately beneath
                    // that class scope. If there is no such combo, a no-arg super() is invalid.
                    var scopes = unit.Scope;
                    ClassScope classScope = null;
                    FunctionScope funcScope = null;
                    foreach (var s in scopes.EnumerateTowardsGlobal) {
                        funcScope = s as FunctionScope;
                        if (funcScope != null) {
                            classScope = s.OuterScope as ClassScope;
                            if (classScope != null) {
                                break;
                            }
                        }
                    }

                    if (classScope != null && funcScope != null) {
                        classes = classScope.Class.SelfSet;
                        // Get first arg of function.
                        if (funcScope.Function.FunctionDefinition.Parameters.Count > 0) {
                            instances = classScope.Class.Instance.SelfSet;
                        }
                    }
                }
            } else {
                classes = args[0];
                if (args.Length > 1) {
                    instances = args[1];
                }
            }

            if (classes == null) {
                return NamespaceSet.Empty;
            }

            return unit.Scope.GetOrMakeNodeValue(call, (node) => {
                var res = NamespaceSet.Empty;
                foreach (var classInfo in classes.OfType<ClassInfo>()) {
                    res = res.Add(new SuperInfo(classInfo, instances));
                }
                return res;
            });
        }

        private INamespaceSet ReturnUnionOfInputs(CallExpression call, AnalysisUnit unit, INamespaceSet[] args, NameExpression[] argNames) {
            var res = NamespaceSet.Empty;
            foreach (var set in args) {
                res = res.Union(set);
            }
            return res;
        }

        internal Namespace GetCached(object key, Func<Namespace> maker) {
            Namespace result;
            if (!_itemCache.TryGetValue(key, out result)) {
                // Set the key to prevent recursion
                _itemCache[key] = null;
                _itemCache[key] = result = maker();
            }
            return result;
        }

        internal BuiltinModule BuiltinModule {
            get { return _builtinModule; }
        }

        internal BuiltinInstanceInfo GetInstance(IPythonType type) {
            return GetBuiltinType(type).Instance;
        }

        internal BuiltinClassInfo GetBuiltinType(IPythonType type) {
            return (BuiltinClassInfo)GetCached(type,
                () => MakeBuiltinType(type)
            );
        }

        private BuiltinClassInfo MakeBuiltinType(IPythonType type) {
            switch (type.TypeId) {
                case BuiltinTypeId.List: return new ListBuiltinClassInfo(type, this);
                case BuiltinTypeId.Tuple: return new TupleBuiltinClassInfo(type, this);
                case BuiltinTypeId.Object: return new ObjectBuiltinClassInfo(type, this);
                default: return new BuiltinClassInfo(type, this);
            }
        }

        internal INamespaceSet GetNamespacesFromObjects(object objects) {
            var typeList = objects as IEnumerable<object>;
            if (typeList == null) {
                return NamespaceSet.Empty;
            }
            return NamespaceSet.UnionAll(typeList.Select(GetNamespaceFromObjects));
        }

        internal INamespaceSet GetNamespacesFromObjects(IEnumerable<IPythonType> typeList) {
            if (typeList == null) {
                return NamespaceSet.Empty;
            }
            return NamespaceSet.UnionAll(typeList.Select(GetNamespaceFromObjects));
        }

        internal Namespace GetNamespaceFromObjectsThrowOnNull(object attr) {
            if (attr == null) {
                throw new ArgumentNullException("attr");
            }
            return GetNamespaceFromObjects(attr);
        }
        
        internal Namespace GetNamespaceFromObjects(object attr) {
            var attrType = (attr != null) ? attr.GetType() : typeof(NoneType);
            if (attr is IPythonType) {
                return GetBuiltinType((IPythonType)attr);
            } else if (attr is IPythonFunction) {
                var bf = (IPythonFunction)attr;
                return GetCached(attr, () => new BuiltinFunctionInfo(bf, this));
            } else if (attr is IPythonMethodDescriptor) {
                return GetCached(attr, () => new BuiltinMethodInfo((IPythonMethodDescriptor)attr, this));
            } else if (attr is IBuiltinProperty) {
                return GetCached(attr, () => new BuiltinPropertyInfo((IBuiltinProperty)attr, this));
            } else if (attr is IPythonModule) {
                return _modules.GetBuiltinModule((IPythonModule)attr);
            } else if (attr is IPythonEvent) {
                return GetCached(attr, () => new BuiltinEventInfo((IPythonEvent)attr, this));
            } else if (attr is IPythonConstant) {
                return GetConstant((IPythonConstant)attr).First();
            } else if (attrType == typeof(bool) || attrType == typeof(int) || attrType == typeof(Complex) ||
                        attrType == typeof(string) || attrType == typeof(long) || attrType == typeof(double) ||
                        attr == null) {
                return GetConstant(attr).First();
            } else if (attr is IMemberContainer) {
                return GetCached(attr, () => new ReflectedNamespace((IMemberContainer)attr, this));
            } else if (attr is IPythonMultipleMembers) {
                IPythonMultipleMembers multMembers = (IPythonMultipleMembers)attr;
                var members = multMembers.Members;
                return GetCached(attr, () => {
                    Namespace[] nses = new Namespace[members.Count];
                    for (int i = 0; i < members.Count; i++) {
                        nses[i] = GetNamespaceFromObjects(members[i]);
                    }
                    return new MultipleMemberInfo(nses);
                }
                );
            } else {
                var pyAttrType = GetTypeFromObject(attr);
                Debug.Assert(pyAttrType != null);
                return GetBuiltinType(pyAttrType).Instance;
            }
        }

        internal IDictionary<string, INamespaceSet> GetAllMembers(IMemberContainer container, IModuleContext moduleContext) {
            var names = container.GetMemberNames(moduleContext);
            var result = new Dictionary<string, INamespaceSet>();
            foreach (var name in names) {
                result[name] = GetNamespaceFromObjects(container.GetMember(moduleContext, name));
            }

            return result;
        }

        internal ModuleTable Modules {
            get { return _modules; }
        }

        internal ConcurrentDictionary<string, ModuleInfo> ModulesByFilename {
            get { return _modulesByFilename; }
        }

        internal INamespaceSet GetConstant(IPythonConstant value) {
            object key = value ?? _nullKey;
            return GetCached(key, () => new ConstantInfo(value, this)).SelfSet;
        }

        internal INamespaceSet GetConstant(object value) {
            object key = value ?? _nullKey;
            return GetCached(key, () => new ConstantInfo(value, this)).SelfSet;
        }

        private static void Update<K, V>(IDictionary<K, V> dict, IDictionary<K, V> newValues) {
            foreach (var kvp in newValues) {
                dict[kvp.Key] = kvp.Value;
            }
        }

        internal IPythonType GetTypeFromObject(object value) {
            if (value == null) {
                return Types[BuiltinTypeId.NoneType];
            }
            switch (Type.GetTypeCode(value.GetType())) {
                case TypeCode.Boolean: return Types[BuiltinTypeId.Bool];
                case TypeCode.Double: return Types[BuiltinTypeId.Float];
                case TypeCode.Int32: return Types[BuiltinTypeId.Int];
                case TypeCode.String: return Types[BuiltinTypeId.Unicode];
                case TypeCode.Object:
                    if (value.GetType() == typeof(Complex)) {
                        return Types[BuiltinTypeId.Complex];
                    } else if (value.GetType() == typeof(AsciiString)) {
                        return Types[BuiltinTypeId.Bytes];
                    } else if (value.GetType() == typeof(BigInteger)) {
                        return Types[BuiltinTypeId.Long];
                    } else if (value.GetType() == typeof(Ellipsis)) {
                        return Types[BuiltinTypeId.Ellipsis];
                    }
                    break;
            }

            throw new InvalidOperationException();
        }

        internal BuiltinClassInfo MakeGenericType(IAdvancedPythonType clrType, params IPythonType[] clrIndexType) {
            var res = clrType.MakeGenericType(clrIndexType);

            return (BuiltinClassInfo)GetNamespaceFromObjects(res);
        }

        #endregion

        #region IGroupableAnalysisProject Members

        void IGroupableAnalysisProject.AnalyzeQueuedEntries(CancellationToken cancel) {
            if (cancel.IsCancellationRequested) {
                return;
            }
            new DDG().Analyze(Queue, cancel, _reportQueueSize, _reportQueueInterval);
        }

        #endregion

        /// <summary>
        /// Specifies a callback to invoke to provide feedback on the number of
        /// items being processed.
        /// </summary>
        public void SetQueueReporting(Action<int> reportFunction, int interval = 1) {
            _reportQueueSize = reportFunction;
            _reportQueueInterval = interval;
        }

        /// <summary>
        /// Adds a directory to the list of directories being analyzed.
        /// 
        /// This method is thread safe.
        /// </summary>
        public void AddAnalysisDirectory(string dir) {
            var dirsChanged = AnalysisDirectoriesChanged;
            bool added;
            lock (_analysisDirs) {
                added = _analysisDirs.Add(dir);
            }
            if (added && dirsChanged != null) {
                dirsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Removes a directory from the list of directories being analyzed.
        /// 
        /// This method is thread safe.
        /// 
        /// New in 1.1.
        /// </summary>
        public void RemoveAnalysisDirectory(string dir) {
            var dirsChanged = AnalysisDirectoriesChanged;
            bool removed;
            lock (_analysisDirs) {
                removed = _analysisDirs.Remove(dir);
            }
            if (removed && dirsChanged != null) {
                dirsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Event fired when the analysis directories have changed.  
        /// 
        /// This event can be fired on any thread.
        /// 
        /// New in 1.1.
        /// </summary>
        public event EventHandler AnalysisDirectoriesChanged;

        #region IDisposable Members

        void IDisposable.Dispose() {
            IDisposable interpreter = _interpreter as IDisposable;
            if (interpreter != null) {
                interpreter.Dispose();
            }
        }

        #endregion

        /// <summary>
        /// Processes any delayed specialization for when a module is added for the 1st time.
        /// </summary>
        /// <param name="moduleName"></param>
        private void DoDelayedSpecialization(string moduleName) {
            lock (_specializationInfo) {
                List<SpecializationInfo> specInfo;
                if (_specializationInfo.TryGetValue(moduleName, out specInfo)) {
                    foreach (var curSpec in specInfo) {
                        SpecializeFunction(curSpec.ModuleName, curSpec.Name, curSpec.Delegate, curSpec.Analyze, save: false);
                    }
                }
            }
        }

        private void SaveDelayedSpecialization(string moduleName, string name, Func<CallExpression, AnalysisUnit, INamespaceSet[], NameExpression[], INamespaceSet> dlg, bool analyze, string realModName) {
            lock (_specializationInfo) {
                List<SpecializationInfo> specList;
                if (!_specializationInfo.TryGetValue(realModName ?? moduleName, out specList)) {
                    _specializationInfo[realModName ?? moduleName] = specList = new List<SpecializationInfo>();
                }

                specList.Add(new SpecializationInfo(moduleName, name, dlg, analyze));
            }
        }

        class SpecializationInfo {
            public readonly string Name, ModuleName;
            public readonly Func<CallExpression, AnalysisUnit, INamespaceSet[], NameExpression[], INamespaceSet> Delegate;
            public readonly bool Analyze;

            public SpecializationInfo(string moduleName, string name, Func<CallExpression, AnalysisUnit, INamespaceSet[], NameExpression[], INamespaceSet> dlg, bool analyze) {
                ModuleName = moduleName;
                Name = name;
                Delegate = dlg;
                Analyze = analyze;
            }
        }
    }
}