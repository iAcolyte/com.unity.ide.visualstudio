using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Unity.CodeEditor;

namespace Microsoft.Unity.VisualStudio.Editor
{
    // Advanced filters "addons"
    public partial class VisualStudioEditor : IExternalCodeEditor
    {
        private class AssemblyWrapper
        {
            internal string PackageId;
            internal string Path;
            internal string Id;
            internal string DisplayName;
        }

        private class PackageWrapper
        {
            internal string Id;
            internal string DisplayName;
            internal List<AssemblyWrapper> Assemblies;
            internal ProjectGenerationFlag Source;
        }

        private Dictionary<ProjectGenerationFlag, bool> _showAdvancedFilters = new();
        private ProjectGenerationFlag _cachedFlag;
        private Dictionary<string, bool> _packageFilter;
        private Dictionary<string, bool> _assemblyFilter;
        private List<PackageWrapper> _packageAssemblyHierarchy;
        private Dictionary<ProjectGenerationFlag, List<PackageWrapper>> _packageAssemblyHierarchyByGenerationFlag;

        private void EnsureAdvancedFiltersCache(IVisualStudioInstallation installation)
        {
            if (_packageFilter == null || _cachedFlag != installation.ProjectGenerator.AssemblyNameProvider.ProjectGenerationFlag)
                InitializeAdvancedFiltersCache(installation);
        }

        private void InitializeAdvancedFiltersCache(IVisualStudioInstallation installation)
        {
            _cachedFlag = installation.ProjectGenerator.AssemblyNameProvider.ProjectGenerationFlag;

            _packageFilter = CreateFilterDictionary(installation.ProjectGenerator.ExcludedPackages);
            _assemblyFilter = CreateFilterDictionary(installation.ProjectGenerator.ExcludedAssemblies);

            var eligiblePackages = installation.ProjectGenerator.PackagesFilteredByProjectGenerationFlags
                .Select(p => new PackageWrapper
                {
                    Id = p.name,
                    DisplayName = string.IsNullOrWhiteSpace(p.displayName) ? p.name : p.displayName,
                    Source = ProjectGenerationFlagFromPackageSource(p.source)
                })
                .OrderBy(ph => ph.DisplayName);

            var filteredPackages = installation.ProjectGenerator.PackagesFilteredByProjectGenerationFlags
                .Where(p => installation.ProjectGenerator.ExcludedPackages.Contains(p.name) == false)
                .ToList();

            var eligibleAssemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies()
                .Select(a =>
                {
                    var assemblyPath = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name);
                    if (assemblyPath == null)
                        // "Assembly-CSharp" and the like... we'll just ignore them
                        return null;

                    var asset = AssetDatabase.LoadAssetAtPath<UnityEditorInternal.AssemblyDefinitionAsset>(assemblyPath);

                    var assemblyName = Path.GetFileName(assemblyPath);

                    var package = installation.ProjectGenerator.AssemblyNameProvider.FindForAssetPath(assemblyPath);
                    if (package == null)
                        // .asmdef in /Assets, so no package
                        return new AssemblyWrapper { Id = assemblyName, Path = assemblyPath, DisplayName = assemblyName };

                    // .asmdef within a package
                    return new AssemblyWrapper { PackageId = package.name, Id = assemblyName, Path = assemblyPath, DisplayName = assemblyName };
                })
                .Where(ah => ah != null)
                .OrderBy(ah => ah.DisplayName);

            // Join by package id
            _packageAssemblyHierarchy = eligiblePackages.GroupJoin(eligibleAssemblies.Where(a => a != null && a.PackageId != null),
                p => p.Id, a => a.PackageId,
                (p, aa) => new PackageWrapper { Id = p.Id, DisplayName = p.DisplayName, Assemblies = aa.ToList(), Source = p.Source })
                .Where(p => p.Assemblies.Any())
                .ToList();

            // Prepend "empty package" containing the .asmdefs in Assets folder
            var assetsAssemblies = eligibleAssemblies.Where(a => a != null && a.PackageId == null).ToList();
            if (assetsAssemblies.Count > 0)
                _packageAssemblyHierarchy.Insert(0, new PackageWrapper { Assemblies = assetsAssemblies });

            _packageAssemblyHierarchyByGenerationFlag = _packageAssemblyHierarchy.GroupBy(p => p.Source).ToDictionary(pg => pg.Key, pg => pg.ToList());
        }

        private ProjectGenerationFlag ProjectGenerationFlagFromPackageSource(PackageSource source)
        {
            switch (source)
            {
                case PackageSource.Unknown:
                    return ProjectGenerationFlag.None;

                default:
                    return Enum.Parse<ProjectGenerationFlag>(source.ToString());
            }
        }

        private Dictionary<string, bool> CreateFilterDictionary(IList<string> excludedPackages)
        {
            return excludedPackages?
                .Where(p => string.IsNullOrWhiteSpace(p) == false)
                .ToDictionary(p => p, _ => false)
                ?? new Dictionary<string, bool>();
        }

        private void WriteBackFilters(IVisualStudioInstallation installation)
        {
            installation.ProjectGenerator.ExcludedPackages = _packageFilter
                .Where(kvp => kvp.Value == false)
                .Select(kvp => kvp.Key)
                .ToList();

            installation.ProjectGenerator.ExcludedAssemblies = _assemblyFilter
                .Where(kvp => kvp.Value == false)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        private string FormatPackageCount(int includedCount, int count) => $"{includedCount}/{count} package{(count == 1 ? "" : "s")}";
        private string FormatAssemblyCount(int includedCount, int count) => $"{includedCount}/{count} assembl{(count == 1 ? "y" : "ies")}";

        private bool DrawAdvancedFiltersFoldout(ProjectGenerationFlag preference, bool isEnabled, IVisualStudioInstallation installation, List<PackageWrapper> packages, int assemblyCount, int includedAssemblyCount)
        {
            var packageCount = packages?.Count ?? 0;
            var includedPackageCount = packages?.Count(p => installation.ProjectGenerator.ExcludedPackages.Contains(p.Id) == false) ?? 0;

            var guiContent = isEnabled ? new GUIContent($"{FormatPackageCount(includedPackageCount, packageCount)}, {FormatAssemblyCount(includedAssemblyCount, assemblyCount)}") : GUIContent.none;
            var isFoldoutEnabled = isEnabled && packageCount > 0;

            EditorGUI.BeginDisabledGroup(isFoldoutEnabled == false);
            _showAdvancedFilters.TryGetValue(preference, out var showAdvancedFilters);
            var isFoldoutExpanded = showAdvancedFilters && isEnabled && packageCount > 0;
            isFoldoutExpanded = EditorGUILayout.Foldout(isFoldoutExpanded, guiContent, toggleOnLabelClick: true);
            if (isFoldoutEnabled)
            {
                _showAdvancedFilters[preference] = isFoldoutExpanded;
            }
            EditorGUI.EndDisabledGroup();

            return isFoldoutExpanded;
        }

        private void DrawAdvancedFilters(ProjectGenerationFlag preference, IVisualStudioInstallation installation)
        {
            var isDirty = false;

            foreach (var package in _packageAssemblyHierarchy)
            {
                if (package.Source != preference)
                    continue;

                var assemblyCount = package.Assemblies.Count;
                var includedAssemblyCount = package.Assemblies.Count(a => installation.ProjectGenerator.ExcludedAssemblies.Contains(a.Id) == false);

                bool isEnabled = true;
                if (_packageFilter.TryGetValue(package.Id, out var wasEnabled) == false)
                    _packageFilter.Add(package.Id, wasEnabled = true);

                isEnabled = DrawToggle(new GUIContent(package.DisplayName), wasEnabled, assemblyCount > includedAssemblyCount);

                if (isEnabled != wasEnabled)
                {
                    _packageFilter[package.Id] = isEnabled;
                    isDirty = true;
                }

                EditorGUI.indentLevel++;
                if (isEnabled)
                {
                    isDirty = DrawAssemblyFilters(package) || isDirty;
                }
                EditorGUI.indentLevel--;
            }

            if (isDirty)
            {
                WriteBackFilters(installation);
            }
        }

        private void DrawAssetAssemblies(IVisualStudioInstallation installation)
        {
            var assetsPackage = _packageAssemblyHierarchyByGenerationFlag[ProjectGenerationFlag.None].First();
            var assemblyCount = assetsPackage.Assemblies.Count();
            var includedAssemblyCount = assetsPackage.Assemblies.Count(a => installation.ProjectGenerator.ExcludedAssemblies.Contains(a.Id) == false);

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(true);
            if (assemblyCount > includedAssemblyCount)
                EditorGUI.showMixedValue = true;
            EditorGUILayout.Toggle(new GUIContent("Assemblies from Assets"), true, GUILayout.ExpandWidth(false));
            EditorGUI.showMixedValue = false;
            EditorGUI.EndDisabledGroup();

            _showAdvancedFilters.TryGetValue(ProjectGenerationFlag.None, out var isFoldoutExpanded);
            _showAdvancedFilters[ProjectGenerationFlag.None] = EditorGUILayout.Foldout(isFoldoutExpanded, FormatAssemblyCount(includedAssemblyCount, assemblyCount), toggleOnLabelClick: true);

            EditorGUILayout.EndHorizontal();

            if (_showAdvancedFilters[ProjectGenerationFlag.None] == false)
                return;

            EditorGUI.indentLevel++;
            var isDirty = DrawAssemblyFilters(_packageAssemblyHierarchyByGenerationFlag[ProjectGenerationFlag.None].First());
            EditorGUI.indentLevel--;

            if (isDirty)
            {
                WriteBackFilters(installation);
            }
        }

        private bool DrawAssemblyFilters(PackageWrapper package)
        {
            if (package.Assemblies == null)
                return false;

            var isDirty = false;
            foreach (var assembly in package.Assemblies)
            {
                if (_assemblyFilter.TryGetValue(assembly.Id, out var wasEnabled) == false)
                    _assemblyFilter.Add(assembly.Id, wasEnabled = true);

                bool isEnabled = DrawToggle(new GUIContent(assembly.DisplayName), wasEnabled);

                if (isEnabled != wasEnabled)
                {
                    _assemblyFilter[assembly.Id] = isEnabled;
                    isDirty = true;
                }
            }
            return isDirty;
        }

        private static bool DrawToggle(GUIContent label, bool wasEnabled, bool showMixedValue = false, params GUILayoutOption[] options)
        {
            EditorGUI.showMixedValue = wasEnabled && showMixedValue;

            EditorGUI.BeginChangeCheck();
            var isEnabled = wasEnabled;
            EditorGUILayout.Toggle(label, isEnabled, options);
            //EditorGUILayout.BeginHorizontal();
            //EditorGUILayout.LabelField(label, GUILayout.Width(260 - EditorGUI.indentLevel * 15));
            //EditorGUILayout.Toggle(wasEnabled, GUILayout.Width(32), GUILayout.ExpandWidth(true));
            //EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
                isEnabled = !wasEnabled;
            EditorGUI.showMixedValue = false;

            if (GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                if (Event.current.shift)
                    isEnabled = false;
                else if (Event.current.control)
                    isEnabled = true;
            }
            return isEnabled;
        }

        private void DrawResetFiltersButton(IVisualStudioInstallation installation)
        {
            var rect = EditorGUILayout.GetControlRect();
            rect.width = 252;
            rect.x -= 20;
            EditorGUI.BeginDisabledGroup(_packageFilter.Count == 0 && _assemblyFilter.Count == 0);
            if (GUI.Button(rect, "Reset filters"))
            {
                _packageFilter = new();
                _assemblyFilter = new();
                _showAdvancedFilters = new();
                WriteBackFilters(installation);
            }
            EditorGUI.EndDisabledGroup();
        }
    }
}
