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

		private bool _showAdvancedFilters;
		private ProjectGenerationFlag _cachedFlag;
		private Dictionary<string, bool> _packageFilter;
		private Dictionary<string, bool> _assemblyFilter;
		private List<PackageWrapper> _packageAssemblyHierarchy;

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
			SettingsButton(ProjectGenerationFlag.Embedded, "Embedded packages", "", installation);
			SettingsButton(ProjectGenerationFlag.Local, "Local packages", "", installation);
			SettingsButton(ProjectGenerationFlag.Registry, "Registry packages", "", installation);
			SettingsButton(ProjectGenerationFlag.Git, "Git packages", "", installation);
			SettingsButton(ProjectGenerationFlag.BuiltIn, "Built-in packages", "", installation);
			SettingsButton(ProjectGenerationFlag.LocalTarBall, "Local tarball", "", installation);
			SettingsButton(ProjectGenerationFlag.Unknown, "Packages from unknown sources", "", installation);
			SettingsButton(ProjectGenerationFlag.PlayerAssemblies, "Player projects", "For each player project generate an additional csproj with the name 'project-player.csproj'", installation);
			
			DrawAdvancedFilters(installation);

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
			internal IEnumerable<AssemblyWrapper> Assemblies;
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
				.Select(p => new PackageWrapper { Id = p.name, DisplayName = string.IsNullOrWhiteSpace(p.displayName) ? p.name : p.displayName })
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
				(p, aa) => new PackageWrapper { Id = p.Id, DisplayName = p.DisplayName, Assemblies = aa.ToList() })
				.Where(p => p.Assemblies.Any())
				.ToList();

			// Prepend "empty package" containing the .asmdefs in Assets folder
			var assetsAssemblies = eligibleAssemblies.Where(a => a != null && a.PackageId == null).ToList();
			if (assetsAssemblies.Count > 0)
				_packageAssemblyHierarchy.Insert(0, new PackageWrapper { DisplayName = "Assets", Assemblies = assetsAssemblies });
		}

		private Dictionary<string, bool> CreateFilterDictionary(IList<string> excludedPackages)
		{
			return excludedPackages?
				.Where(p => string.IsNullOrWhiteSpace(p) == false)
				.ToDictionary(p => p, _ => false)
				?? new Dictionary<string, bool>();
		}

		private void DrawAdvancedFilters(IVisualStudioInstallation installation)
		{
			_showAdvancedFilters = EditorGUILayout.BeginFoldoutHeaderGroup(_showAdvancedFilters, new GUIContent("Advanced filters"));
			if (_showAdvancedFilters)
			{
				EnsureAdvancedFiltersCache(installation);

				EditorGUILayout.HelpBox("Hold Ctrl/Shift while moving the cursor over checkboxes below to bulk add/remove checkmarks", MessageType.Info);

				var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
				rect.width = 252;
				if (GUI.Button(rect, "Reset filters"))
				{
                    installation.ProjectGenerator.ExcludedPackages = null;
                    installation.ProjectGenerator.ExcludedAssemblies = null;
					InitializeAdvancedFiltersCache(installation);
				}

				var isDirty = false;
				EditorGUI.indentLevel++;

				foreach (var package in _packageAssemblyHierarchy)
				{
					bool isEnabled = true;
					if (package.Id == null)
					{
						// Draw disabled toggle (for Assets)
						EditorGUI.BeginDisabledGroup(true);
						EditorGUILayout.Toggle(package.DisplayName, isEnabled);
						EditorGUI.EndDisabledGroup();
					}
					else
					{
						if (_packageFilter.TryGetValue(package.Id, out var wasEnabled) == false)
							_packageFilter.Add(package.Id, wasEnabled = true);

						isEnabled = DrawToggle(package.DisplayName, wasEnabled);

						if (isEnabled != wasEnabled)
						{
							_packageFilter[package.Id] = isEnabled;
							isDirty = true;
						}
					}
					EditorGUI.indentLevel++;
					if (isEnabled)
					{
						isDirty = DrawAssemblyFilters(package) || isDirty;
					}
					EditorGUI.indentLevel--;

				}
				EditorGUI.indentLevel--;

				if (isDirty)
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
			}

			EditorGUILayout.EndFoldoutHeaderGroup();
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

		private static void SettingsButton(ProjectGenerationFlag preference, string guiMessage, string toolTip, IVisualStudioInstallation installation)
		{
			var generator = installation.ProjectGenerator;
			var prevValue = generator.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(preference);

			var newValue = EditorGUILayout.Toggle(new GUIContent(guiMessage, toolTip), prevValue);
			if (newValue != prevValue)
				generator.AssemblyNameProvider.ToggleProjectGeneration(preference);
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

			if (!Discovery.TryDiscoverInstallation(editorPath, out var installation)) {
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
