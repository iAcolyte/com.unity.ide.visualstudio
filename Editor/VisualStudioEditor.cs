/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Unity.CodeEditor;
using System.Collections.Generic;

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

		CodeEditor.Installation[] IExternalCodeEditor.Installations => _discoverInstallations.Result
			.Select(i => i.ToCodeEditorInstallation())
			.ToArray();

		private static readonly AsyncOperation<IVisualStudioInstallation[]> _discoverInstallations;

		private readonly IGenerator _generator = new ProjectGeneration();
		private bool _showAdvancedFilters;
        private ProjectGenerationFlag _cachedFlag;
        private Dictionary<string, bool> _packageFilter;
		private Dictionary<string, bool> _assemblyFilter;
		private List<PackageWrapper> _packageAssemblyHierarchy;

		static VisualStudioEditor()
		{
			if (!UnityInstallation.IsMainUnityEditorProcess)
				return;

			if (IsWindows)
				Discovery.FindVSWhere();

			CodeEditor.Register(new VisualStudioEditor());

			_discoverInstallations = AsyncOperation<IVisualStudioInstallation[]>.Run(DiscoverInstallations);
		}

		private static IVisualStudioInstallation[] DiscoverInstallations()
		{
			try
			{
				return Discovery
					.GetVisualStudioInstallations()
					.ToArray();
			}
			catch (Exception ex)
			{
				UnityEngine.Debug.LogError($"Error detecting Visual Studio installations: {ex}");
				return Array.Empty<IVisualStudioInstallation>();
			}
		}

		internal static bool IsEnabled => CodeEditor.CurrentEditor is VisualStudioEditor && UnityInstallation.IsMainUnityEditorProcess;

		// this one seems legacy and not used anymore
		// keeping it for now given it is public, so we need a major bump to remove it 
		public void CreateIfDoesntExist()
		{
			if (!_generator.HasSolutionBeenGenerated())
				_generator.Sync();
		}

		public void Initialize(string editorInstallationPath)
		{
		}

		internal virtual bool TryGetVisualStudioInstallationForPath(string editorPath, bool searchInstallations, out IVisualStudioInstallation installation)
		{
			if (searchInstallations)
			{
				// lookup for well known installations
				foreach (var candidate in _discoverInstallations.Result)
				{
					if (!string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(candidate.Path), StringComparison.OrdinalIgnoreCase))
						continue;

					installation = candidate;
					return true;
				}
			}

			return Discovery.TryDiscoverInstallation(editorPath, out installation);
		}

		public virtual bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
		{
			var result = TryGetVisualStudioInstallationForPath(editorPath, searchInstallations: false, out var vsi);
			installation = vsi == null ? default : vsi.ToCodeEditorInstallation();
			return result;
		}

		public void OnGUI()
		{
			GUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();

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
			SettingsButton(ProjectGenerationFlag.Embedded, "Embedded packages", "");
			SettingsButton(ProjectGenerationFlag.Local, "Local packages", "");
			SettingsButton(ProjectGenerationFlag.Registry, "Registry packages", "");
			SettingsButton(ProjectGenerationFlag.Git, "Git packages", "");
			SettingsButton(ProjectGenerationFlag.BuiltIn, "Built-in packages", "");
			SettingsButton(ProjectGenerationFlag.LocalTarBall, "Local tarball", "");
			SettingsButton(ProjectGenerationFlag.Unknown, "Packages from unknown sources", "");
			SettingsButton(ProjectGenerationFlag.PlayerAssemblies, "Player projects", "For each player project generate an additional csproj with the name 'project-player.csproj'");

			DrawAdvancedFilters();

			RegenerateProjectFiles();
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

		private void EnsureAdvancedFiltersCache()
		{
			if (_packageFilter == null || _cachedFlag != _generator.AssemblyNameProvider.ProjectGenerationFlag)
				InitializeAdvancedFiltersCache();
		}

		private void InitializeAdvancedFiltersCache()
		{
			_cachedFlag = _generator.AssemblyNameProvider.ProjectGenerationFlag;

			_packageFilter = CreateFilterDictionary(_generator.ExcludedPackages);
			_assemblyFilter = CreateFilterDictionary(_generator.ExcludedAssemblies);

			var eligiblePackages = _generator.PackagesFilteredByProjectGenerationFlags
				.Select(p => new PackageWrapper { Id = p.name, DisplayName = string.IsNullOrWhiteSpace(p.displayName) ? p.name : p.displayName })
				.OrderBy(ph => ph.DisplayName);

			var filteredPackages = _generator.PackagesFilteredByProjectGenerationFlags
				.Where(p => _generator.ExcludedPackages.Contains(p.name) == false)
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

					var package = _generator.AssemblyNameProvider.FindForAssetPath(assemblyPath);
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

		private void DrawAdvancedFilters()
		{
			_showAdvancedFilters = EditorGUILayout.BeginFoldoutHeaderGroup(_showAdvancedFilters, new GUIContent("Advanced filters"));
			if (_showAdvancedFilters)
            {
                EnsureAdvancedFiltersCache();

                var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
                rect.width = 252;
                if (GUI.Button(rect, "Reset filters"))
                {
                    _generator.ExcludedPackages = null;
                    _generator.ExcludedAssemblies = null;
                    InitializeAdvancedFiltersCache();
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
                    _generator.ExcludedPackages = _packageFilter
                        .Where(kvp => kvp.Value == false)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    _generator.ExcludedAssemblies = _assemblyFilter
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

		void RegenerateProjectFiles()
		{
			var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
			rect.width = 252;
			if (GUI.Button(rect, "Regenerate project files"))
			{
				_generator.Sync();
			}
		}

		void SettingsButton(ProjectGenerationFlag preference, string guiMessage, string toolTip)
		{
			var prevValue = _generator.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(preference);
			var newValue = EditorGUILayout.Toggle(new GUIContent(guiMessage, toolTip), prevValue);
			if (newValue != prevValue)
			{
				_generator.AssemblyNameProvider.ToggleProjectGeneration(preference);
			}
		}

		public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
		{
			_generator.SyncIfNeeded(addedFiles.Union(deletedFiles).Union(movedFiles).Union(movedFromFiles), importedFiles);

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

				UnityEngine.Debug.LogWarning($"Unity is only able to load mdb or portable-pdb symbols. {file} is using a legacy pdb format.");
			}
		}

		public void SyncAll()
		{
			_generator.Sync();
		}

		bool IsSupportedPath(string path)
		{
			// Path is empty with "Open C# Project", as we only want to open the solution without specific files
			if (string.IsNullOrEmpty(path))
				return true;

			// cs, uxml, uss, shader, compute, cginc, hlsl, glslinc, template are part of Unity builtin extensions
			// txt, xml, fnt, cd are -often- par of Unity user extensions
			// asdmdef is mandatory included
			if (_generator.IsSupportedFile(path))
				return true;

			return false;
		}

		private static void CheckCurrentEditorInstallation()
		{
			var editorPath = CodeEditor.CurrentEditorInstallation;
			try
			{
				if (Discovery.TryDiscoverInstallation(editorPath, out _))
					return;
			}
			catch (IOException)
			{
			}

			UnityEngine.Debug.LogWarning($"Visual Studio executable {editorPath} is not found. Please change your settings in Edit > Preferences > External Tools.");
		}

		public bool OpenProject(string path, int line, int column)
		{
			CheckCurrentEditorInstallation();

			if (!IsSupportedPath(path))
				return false;

			if (!IsProjectGeneratedFor(path, out var missingFlag))
				UnityEngine.Debug.LogWarning($"You are trying to open {path} outside a generated project. This might cause problems with IntelliSense and debugging. To avoid this, you can change your .csproj preferences in Edit > Preferences > External Tools and enable {GetProjectGenerationFlagDescription(missingFlag)} generation.");

			if (IsOSX)
				return OpenOSXApp(path, line, column);

			if (IsWindows)
				return OpenWindowsApp(path, line);

			return false;
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

		private bool IsProjectGeneratedFor(string path, out ProjectGenerationFlag missingFlag)
		{
			missingFlag = ProjectGenerationFlag.None;

			// No need to check when opening the whole solution
			if (string.IsNullOrEmpty(path))
				return true;

			// We only want to check for cs scripts
			if (ProjectGeneration.ScriptingLanguageForFile(path) != ScriptingLanguage.CSharp)
				return true;

			// Even on windows, the package manager requires relative path + unix style separators for queries
			var basePath = _generator.ProjectDirectory;
			var relativePath = FileUtility
				.NormalizeWindowsToUnix(path)
				.Replace(basePath, string.Empty)
				.Trim(FileUtility.UnixSeparator);

			var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(relativePath);
			if (packageInfo == null)
				return true;

			var source = packageInfo.source;
			if (!Enum.TryParse<ProjectGenerationFlag>(source.ToString(), out var flag))
				return true;

			if (_generator.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(flag))
				return true;

			// Return false if we found a source not flagged for generation
			missingFlag = flag;
			return false;
		}

		private bool OpenWindowsApp(string path, int line)
		{
			var progpath = FileUtility.GetPackageAssetFullPath("Editor", "COMIntegration", "Release", "COMIntegration.exe");

			if (string.IsNullOrWhiteSpace(progpath))
				return false;

			string absolutePath = "";
			if (!string.IsNullOrWhiteSpace(path))
			{
				absolutePath = Path.GetFullPath(path);
			}

			// We remove all invalid chars from the solution filename, but we cannot prevent the user from using a specific path for the Unity project
			// So process the fullpath to make it compatible with VS
			var solution = GetOrGenerateSolutionFile(path);
			if (!string.IsNullOrWhiteSpace(solution))
			{
				solution = $"\"{solution}\"";
				solution = solution.Replace("^", "^^");
			}

			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = progpath,
					Arguments = $"\"{CodeEditor.CurrentEditorInstallation}\" {solution} \"{absolutePath}\" {line}",
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					StandardOutputEncoding = System.Text.Encoding.Unicode,
					RedirectStandardError = true,
					StandardErrorEncoding = System.Text.Encoding.Unicode,
				}
			};
			var result = process.Start();

			while (!process.StandardOutput.EndOfStream)
			{
				var outputLine = process.StandardOutput.ReadLine();
				if (outputLine == "displayProgressBar")
				{
					EditorUtility.DisplayProgressBar("Opening Visual Studio", "Starting up Visual Studio, this might take some time.", .5f);
				}

				if (outputLine == "clearprogressbar")
				{
					EditorUtility.ClearProgressBar();
				}
			}

			var errorOutput = process.StandardError.ReadToEnd();
			if (!string.IsNullOrEmpty(errorOutput))
			{
				Console.WriteLine("Error: \n" + errorOutput);
			}

			process.WaitForExit();
			return result;
		}

		[DllImport("AppleEventIntegration")]
		static extern bool OpenVisualStudio(string appPath, string solutionPath, string filePath, int line);

		bool OpenOSXApp(string path, int line, int column)
		{
			string absolutePath = "";
			if (!string.IsNullOrWhiteSpace(path))
			{
				absolutePath = Path.GetFullPath(path);
			}

			var solution = GetOrGenerateSolutionFile(path);
			return OpenVisualStudio(CodeEditor.CurrentEditorInstallation, solution, absolutePath, line);
		}

		private string GetOrGenerateSolutionFile(string path)
		{
			_generator.Sync();
			return _generator.SolutionFile();
		}
	}
}
