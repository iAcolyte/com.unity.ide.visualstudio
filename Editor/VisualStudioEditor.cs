/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Unity.CodeEditor;
using Debug = UnityEngine.Debug;

[assembly: InternalsVisibleTo("Unity.VisualStudio.EditorTests")]
[assembly: InternalsVisibleTo("Unity.VisualStudio.Standalone.EditorTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Microsoft.Unity.VisualStudio.Editor
{
    [InitializeOnLoad]
    public class VisualStudioEditor : IExternalCodeEditor
    {
        internal static bool IsOSX => Application.platform == RuntimePlatform.OSXEditor;
        internal static bool IsWindows => !IsOSX && Path.DirectorySeparatorChar == FileUtility.WinSeparator && Environment.NewLine == "\r\n";

        CodeEditor.Installation[] IExternalCodeEditor.Installations => _discoverInstallations
            .Result
            .Values
            .Select(v => v.ToCodeEditorInstallation())
            .ToArray();

        private static readonly AsyncOperation<Dictionary<string, IVisualStudioInstallation>> _discoverInstallations;

        private Dictionary<ProjectGenerationFlag, bool> _showAdvancedFilters = new();
        private ProjectGenerationFlag _cachedFlag;
        private Dictionary<string, bool> _packageFilter;
        private Dictionary<string, bool> _assemblyFilter;
        private List<PackageWrapper> _packageAssemblyHierarchy;
        private Dictionary<ProjectGenerationFlag, List<PackageWrapper>> _packageAssemblyHierarchyByGenerationFlag;

        static VisualStudioEditor()
        {
            if (!UnityInstallation.IsMainUnityEditorProcess)
                return;

            Discovery.Initialize();
            CodeEditor.Register(new VisualStudioEditor());

            _discoverInstallations = AsyncOperation<Dictionary<string, IVisualStudioInstallation>>.Run(DiscoverInstallations);
        }

#if UNITY_2019_4_OR_NEWER && !UNITY_2020
        [InitializeOnLoadMethod]
        static void LegacyVisualStudioCodePackageDisabler()
        {
            // disable legacy Visual Studio Code packages
            var editor = CodeEditor.Editor.GetCodeEditorForPath("code.cmd");
            if (editor == null)
                return;

            if (editor is VisualStudioEditor)
                return;

            CodeEditor.Unregister(editor);
        }
#endif

        private static Dictionary<string, IVisualStudioInstallation> DiscoverInstallations()
        {
            try
            {
                return Discovery
                    .GetVisualStudioInstallations()
                    .ToDictionary(i => Path.GetFullPath(i.Path), i => i);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error detecting Visual Studio installations: {ex}");
                return new Dictionary<string, IVisualStudioInstallation>();
            }
        }

        internal static bool IsEnabled => CodeEditor.CurrentEditor is VisualStudioEditor && UnityInstallation.IsMainUnityEditorProcess;

        // this one seems legacy and not used anymore
        // keeping it for now given it is public, so we need a major bump to remove it 
        public void CreateIfDoesntExist()
        {
            if (!TryGetVisualStudioInstallationForPath(CodeEditor.CurrentEditorInstallation, true, out var installation))
                return;

            var generator = installation.ProjectGenerator;
            if (!generator.HasSolutionBeenGenerated())
                generator.Sync();
        }

        public void Initialize(string editorInstallationPath)
        {
        }

        internal virtual bool TryGetVisualStudioInstallationForPath(string editorPath, bool lookupDiscoveredInstallations, out IVisualStudioInstallation installation)
        {
            editorPath = Path.GetFullPath(editorPath);

            // lookup for well known installations
            if (lookupDiscoveredInstallations && _discoverInstallations.Result.TryGetValue(editorPath, out installation))
                return true;

            return Discovery.TryDiscoverInstallation(editorPath, out installation);
        }

        public virtual bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            var result = TryGetVisualStudioInstallationForPath(editorPath, lookupDiscoveredInstallations: false, out var vsi);
            installation = vsi?.ToCodeEditorInstallation() ?? default;
            return result;
        }

        public void OnGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (!TryGetVisualStudioInstallationForPath(CodeEditor.CurrentEditorInstallation, true, out var installation))
                return;

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(GetType().Assembly);

            var style = new GUIStyle
            {
                richText = true,
                margin = new RectOffset(0, 4, 0, 0)
            };

            GUILayout.Label($"<size=10><color=grey>{package.displayName} v{package.version} enabled</color></size>", style);
            GUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Generate .csproj files for:");
            EditorGUI.indentLevel++;

            EnsureAdvancedFiltersCache(installation);

            SettingsButton(ProjectGenerationFlag.Embedded, "Embedded packages", "", installation);
            SettingsButton(ProjectGenerationFlag.Local, "Local packages", "", installation);
            SettingsButton(ProjectGenerationFlag.Registry, "Registry packages", "", installation);
            SettingsButton(ProjectGenerationFlag.Git, "Git packages", "", installation);
            SettingsButton(ProjectGenerationFlag.BuiltIn, "Built-in packages", "", installation);
            SettingsButton(ProjectGenerationFlag.LocalTarBall, "Local tarball", "", installation);
            SettingsButton(ProjectGenerationFlag.Unknown, "Packages from unknown sources", "", installation);
            SettingsButton(ProjectGenerationFlag.PlayerAssemblies, "Player projects", "For each player project generate an additional csproj with the name 'project-player.csproj'", installation);

            EditorGUILayout.Space();
            DrawAssetAssemblies(installation);
            EditorGUILayout.Space();

            RegenerateProjectFiles(installation);

            EditorGUI.indentLevel--;
        }

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
            switch(source)
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

        private bool DrawAdvancedFiltersFoldout(ProjectGenerationFlag preference, bool isEnabled, IVisualStudioInstallation installation)
        {
            var packageCount = _packageAssemblyHierarchyByGenerationFlag.TryGetValue(preference, out var packages) ? packages.Count : 0;
            var includedPackageCount = packages?.Count(p => installation.ProjectGenerator.ExcludedPackages.Contains(p.Id) == false) ?? 0;
            var assemblyCount = packages?.Sum(p => p.Assemblies.Count) ?? 0;
            var includedAssemblyCount = assemblyCount - packages?.Sum(p =>
                installation.ProjectGenerator.ExcludedPackages.Contains(p.Id) ?
                    p.Assemblies.Count :
                    p.Assemblies.Count(a => installation.ProjectGenerator.ExcludedAssemblies.Contains(a.Id))) ?? 0;
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

                bool isEnabled = true;
                if (_packageFilter.TryGetValue(package.Id, out var wasEnabled) == false)
                    _packageFilter.Add(package.Id, wasEnabled = true);

                isEnabled = DrawToggle(package.DisplayName, wasEnabled);

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
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Toggle(new GUIContent(".asmdefs from Assets"), true, GUILayout.ExpandWidth(false));
            EditorGUI.EndDisabledGroup();
            var assetsPackage = _packageAssemblyHierarchyByGenerationFlag[ProjectGenerationFlag.None].First();
            var assemblyCount = assetsPackage.Assemblies.Count();
            var includedAssemblyCount = assetsPackage.Assemblies.Count(a => installation.ProjectGenerator.ExcludedAssemblies.Contains(a.Id) == false);
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

                bool isEnabled = DrawToggle(assembly.DisplayName, wasEnabled);

                if (isEnabled != wasEnabled)
                {
                    _assemblyFilter[assembly.Id] = isEnabled;
                    isDirty = true;
                }
            }
            return isDirty;
        }

        private static bool DrawToggle(string label, bool wasEnabled)
        {
            var isEnabled = EditorGUILayout.Toggle(label, wasEnabled);
            if (GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                if (Event.current.shift)
                    isEnabled = false;
                else if (Event.current.control)
                    isEnabled = true;
            }
            return isEnabled;
        }

        private static void RegenerateProjectFiles(IVisualStudioInstallation installation)
        {
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            rect.width = 252;
            if (GUI.Button(rect, "Regenerate project files"))
            {
                installation.ProjectGenerator.Sync();
            }
        }

        private void SettingsButton(ProjectGenerationFlag preference, string guiMessage, string toolTip, IVisualStudioInstallation installation)
        {
            var generator = installation.ProjectGenerator;
            var prevValue = generator.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(preference);

            EditorGUILayout.BeginHorizontal();
            var newValue = EditorGUILayout.Toggle(new GUIContent(guiMessage, toolTip), prevValue, GUILayout.ExpandWidth(false));
            if (newValue != prevValue)
                generator.AssemblyNameProvider.ToggleProjectGeneration(preference);

            bool isFoldoutExpanded = false;
            if (newValue)
            {
                isFoldoutExpanded = DrawAdvancedFiltersFoldout(preference, newValue, installation);
            }
            else
            {
                // draw space to avoid jumping toggles
                EditorGUILayout.Space();
            }
            EditorGUILayout.EndHorizontal();

            if (isFoldoutExpanded == false)
                return;

            EditorGUI.indentLevel++;
            DrawAdvancedFilters(preference, installation);
            EditorGUI.indentLevel--;
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            if (TryGetVisualStudioInstallationForPath(CodeEditor.CurrentEditorInstallation, true, out var installation))
            {
                installation.ProjectGenerator.SyncIfNeeded(addedFiles.Union(deletedFiles).Union(movedFiles).Union(movedFromFiles), importedFiles);
            }

            foreach (var file in importedFiles.Where(a => Path.GetExtension(a) == ".pdb"))
            {
                var pdbFile = FileUtility.GetAssetFullPath(file);

                // skip Unity packages like com.unity.ext.nunit
                if (pdbFile.IndexOf($"{Path.DirectorySeparatorChar}com.unity.", StringComparison.OrdinalIgnoreCase) > 0)
                    continue;

                var asmFile = Path.ChangeExtension(pdbFile, ".dll");
                if (!File.Exists(asmFile) || !Image.IsAssembly(asmFile))
                    continue;

                if (Symbols.IsPortableSymbolFile(pdbFile))
                    continue;

                Debug.LogWarning($"Unity is only able to load mdb or portable-pdb symbols. {file} is using a legacy pdb format.");
            }
        }

        public void SyncAll()
        {
            if (TryGetVisualStudioInstallationForPath(CodeEditor.CurrentEditorInstallation, true, out var installation))
            {
                installation.ProjectGenerator.Sync();
            }
        }

        private static bool IsSupportedPath(string path, IGenerator generator)
        {
            // Path is empty with "Open C# Project", as we only want to open the solution without specific files
            if (string.IsNullOrEmpty(path))
                return true;

            // cs, uxml, uss, shader, compute, cginc, hlsl, glslinc, template are part of Unity builtin extensions
            // txt, xml, fnt, cd are -often- par of Unity user extensions
            // asdmdef is mandatory included
            return generator.IsSupportedFile(path);
        }

        public bool OpenProject(string path, int line, int column)
        {
            var editorPath = CodeEditor.CurrentEditorInstallation;

            if (!Discovery.TryDiscoverInstallation(editorPath, out var installation))
            {
                Debug.LogWarning($"Visual Studio executable {editorPath} is not found. Please change your settings in Edit > Preferences > External Tools.");
                return false;
            }

            var generator = installation.ProjectGenerator;
            if (!IsSupportedPath(path, generator))
                return false;

            if (!IsProjectGeneratedFor(path, generator, out var missingFlag))
                Debug.LogWarning($"You are trying to open {path} outside a generated project. This might cause problems with IntelliSense and debugging. To avoid this, you can change your .csproj preferences in Edit > Preferences > External Tools and enable {GetProjectGenerationFlagDescription(missingFlag)} generation.");

            var solution = GetOrGenerateSolutionFile(generator);
            return installation.Open(path, line, column, solution);
        }

        private static string GetProjectGenerationFlagDescription(ProjectGenerationFlag flag)
        {
            switch (flag)
            {
                case ProjectGenerationFlag.BuiltIn:
                    return "Built-in packages";
                case ProjectGenerationFlag.Embedded:
                    return "Embedded packages";
                case ProjectGenerationFlag.Git:
                    return "Git packages";
                case ProjectGenerationFlag.Local:
                    return "Local packages";
                case ProjectGenerationFlag.LocalTarBall:
                    return "Local tarball";
                case ProjectGenerationFlag.PlayerAssemblies:
                    return "Player projects";
                case ProjectGenerationFlag.Registry:
                    return "Registry packages";
                case ProjectGenerationFlag.Unknown:
                    return "Packages from unknown sources";
                default:
                    return string.Empty;
            }
        }

        private static bool IsProjectGeneratedFor(string path, IGenerator generator, out ProjectGenerationFlag missingFlag)
        {
            missingFlag = ProjectGenerationFlag.None;

            // No need to check when opening the whole solution
            if (string.IsNullOrEmpty(path))
                return true;

            // We only want to check for cs scripts
            if (ProjectGeneration.ScriptingLanguageForFile(path) != ScriptingLanguage.CSharp)
                return true;

            // Even on windows, the package manager requires relative path + unix style separators for queries
            var basePath = generator.ProjectDirectory;
            var relativePath = path
                .NormalizeWindowsToUnix()
                .Replace(basePath, string.Empty)
                .Trim(FileUtility.UnixSeparator);

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(relativePath);
            if (packageInfo == null)
                return true;

            var source = packageInfo.source;
            if (!Enum.TryParse<ProjectGenerationFlag>(source.ToString(), out var flag))
                return true;

            if (generator.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(flag))
                return true;

            // Return false if we found a source not flagged for generation
            missingFlag = flag;
            return false;
        }

        private static string GetOrGenerateSolutionFile(IGenerator generator)
        {
            generator.Sync();
            return generator.SolutionFile();
        }
    }
}
