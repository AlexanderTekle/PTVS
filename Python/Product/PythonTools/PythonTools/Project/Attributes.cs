/* ****************************************************************************
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
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommonSR = Microsoft.VisualStudioTools.Project.SR;

namespace Microsoft.PythonTools.Project {
    internal class SR : CommonSR {
        public const string CanNotCreateWindow = "CanNotCreateWindow";
        public const string ExecutingStartupScript = "ExecutingStartupScript";
        public const string InitializingFromProject = "InitializingFromProject";
        public const string MissingStartupScript = "MissingStartupScript";
        public const string SettingWorkDir = "SettingWorkDir";
        public const string ToolWindowTitle = "ToolWindowTitle";
        public const string UpdatingSearchPath = "UpdatingSearchPath";
        public const string WarningAnalysisNotCurrent = "WarningAnalysisNotCurrent";

        public const string SearchPaths = "SearchPaths";
        public const string SearchPathsDescription = "SearchPathsDescription";
        public const string SearchPathRemoveConfirmation = "SearchPathRemoveConfirmation";

        public const string Interpreters = "Interpreters";
        public const string InterpreterRemoveConfirmation = "InterpreterRemoveConfirmation";
        public const string InterpreterDeleteConfirmation = "InterpreterDeleteConfirmation";
        public const string InterpreterDeleteError = "InterpreterDeleteError";

        public const string InterpreterIdDisplayName = "InterpreterIdDisplayName";
        public const string InterpreterIdDescription = "InterpreterIdDescription";
        public const string InterpreterVersionDisplayName = "InterpreterVersionDisplayName";
        public const string InterpreterVersionDescription = "InterpreterVersionDescription";

        public const string BaseInterpreterDisplayName = "BaseInterpreterDisplayName";
        public const string BaseInterpreterDescription = "BaseInterpreterDescription";
        public const string InstallVirtualEnvAndPip = "InstallVirtualEnvAndPip";
        public const string InstallVirtualEnv = "InstallVirtualEnv";
        public const string InstallPip = "InstallPip";
        public const string PythonToolsForVisualStudio = "PythonToolsForVisualStudio";

        internal static new string GetString(string value) {
            string result = Microsoft.PythonTools.Resources.ResourceManager.GetString(value, CultureInfo.CurrentUICulture) ?? CommonSR.GetString(value);
            if (result == null) {
                Debug.Assert(false, "String resource '" + value + "' is missing");
                result = value;
            }
            return result;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class LocDisplayNameAttribute : DisplayNameAttribute {
        readonly string value;

        public LocDisplayNameAttribute(string name) {
            value = name;
        }

        public override string DisplayName {
            get {
                return SR.GetString(value);
            }
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class SRCategoryAttribute : CategoryAttribute {
        public SRCategoryAttribute(string name) : base(name) { }

        protected override string GetLocalizedString(string value) {
            return SR.GetString(value);
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    internal sealed class SRDescriptionAttribute : DescriptionAttribute {
        readonly string value;

        public SRDescriptionAttribute(string name) {
            value = name;
        }

        public override string Description {
            get {
                return SR.GetString(value);
            }
        }
    }
}