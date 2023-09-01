using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace Microsoft.Unity.VisualStudio.Editor
{
    public partial interface IGenerator
    {
        IList<string> ExcludedPackages { get; set; }
        IList<string> ExcludedAssemblies { get; set; }
        IEnumerable<UnityEditor.PackageManager.PackageInfo> PackagesFilteredByProjectGenerationFlags { get; }

    }

    public partial class ProjectGeneration : IGenerator
	{
        private const string _excludedPackagesPathsKeyFormat = "unity_project_generation_excludedpackages_{0}";
        private static readonly string _excludedPackagesKey = string.Format(_excludedPackagesPathsKeyFormat, PlayerSettings.productGUID);
        private const string _excludedAssembliesPathsKeyFormat = "unity_project_generation_excludedassemblies_{0}";
        private static readonly string _excludedAssembliesKey = string.Format(_excludedAssembliesPathsKeyFormat, PlayerSettings.productGUID);

        List<string> m_ExcludedPackages = GetEditorPrefsStringList(_excludedPackagesKey);
        List<string> m_ExcludedAssemblies = GetEditorPrefsStringList(_excludedAssembliesKey);

        public IList<string> ExcludedPackages
        {
            get => m_ExcludedPackages;
            set
            {
                EditorPrefs.SetString(_excludedPackagesKey, value == null ? "" : string.Join(";", value));
                m_ExcludedPackages = value?.ToList() ?? new List<string>();
            }
        }

        public IList<string> ExcludedAssemblies
        {
            get => m_ExcludedAssemblies;
            set
            {
                EditorPrefs.SetString(_excludedAssembliesKey, value == null ? "" : string.Join(";", value));
                m_ExcludedAssemblies = value?.ToList() ?? new List<string>();
            }
        }

        public IEnumerable<UnityEditor.PackageManager.PackageInfo> PackagesFilteredByProjectGenerationFlags =>
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
            .Where(p => m_AssemblyNameProvider.IsInternalizedPackage(p) == false);

        private static List<string> GetEditorPrefsStringList(string key)
        {
            return EditorPrefs.GetString(key, null)?
                .Split(";", StringSplitOptions.RemoveEmptyEntries)
                .ToList()
                ?? new List<string>();
        }

        private bool ShouldFileBePartOfSolutionDependingOnFilters(string file)
        {
            var packageInfo = m_AssemblyNameProvider.FindForAssetPath(file);
            if (packageInfo != null)
            {
                // Exclude files coming from packages except if they are internalized...
                if (m_AssemblyNameProvider.IsInternalizedPackage(packageInfo))
                    return false;

                // ... or if they are excluded by package name
                if (m_ExcludedPackages.Contains(packageInfo.name))
                    return false;
            }

            if (m_ExcludedAssemblies != null && m_ExcludedAssemblies.Count > 0)
            {
                var containingAssemblyPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(file);
                if (containingAssemblyPath != null)
                {
                    // ... or if they belong to a excluded .asmdef
                    var containingAssemblyFilename = Path.GetFileName(containingAssemblyPath);
                    if (m_ExcludedAssemblies.Contains(containingAssemblyFilename))
                        return false;
                }
            }

            return true;
        }
    }
}
