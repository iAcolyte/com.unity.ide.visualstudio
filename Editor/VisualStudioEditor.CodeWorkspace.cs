using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Unity.CodeEditor;

// Advanced filters "addons"
namespace Microsoft.Unity.VisualStudio.Editor
{
	public partial class VisualStudioEditor : IExternalCodeEditor
	{
		private const string _workspacePathKeyFormat = "unity_project_visualstudiocode_workspacePath_{0}_{1}";
		private static readonly string _workspacePathKey = string.Format(_workspacePathKeyFormat, PlayerSettings.productGUID, Directory.GetCurrentDirectory().GetHashCode());

		private string _workspacePath;
		private string _workspaceAbsolutePath;

		private void InitializeCodeWorkspace()
		{
			_workspacePath = EditorPrefs.GetString(_workspacePathKey, null);
			if (string.IsNullOrEmpty(_workspacePath))
			{
				string cwd = Directory.GetCurrentDirectory();
				_workspacePath = Directory.GetFiles(cwd, "*.code-workspace", SearchOption.TopDirectoryOnly).FirstOrDefault();
				if (_workspacePath == null)
				{
					string parent = System.IO.Directory.GetParent(cwd).FullName;
					_workspacePath = Directory.GetFiles(parent, "*.code-workspace", SearchOption.TopDirectoryOnly).FirstOrDefault();
				}
				if (_workspacePath != null)
				{
					EditorPrefs.SetString(_workspacePathKey, _workspacePath);
				}
			}

			UpdateWorkspacePath();
		}

		private void UpdateWorkspacePath(){
			if (_workspacePath != null && File.Exists(_workspacePath))
			{
				_workspaceAbsolutePath = Path.GetFullPath(_workspacePath);
			}
			else
			{
				_workspaceAbsolutePath = null;
			}
		}

		private void CodeWorkspaceGUI(IVisualStudioInstallation installation){
			if (installation is not VisualStudioCodeInstallation)
				return;

			GUILayout.BeginHorizontal();
			GUILayout.Label("Visual Studio Code Workspace", GUILayout.Width(248));
			string newValue = GUILayout.TextField(_workspacePath, GUILayout.ExpandWidth(true));
			if (newValue != _workspacePath)
			{
				if (string.IsNullOrEmpty(newValue))
					EditorPrefs.DeleteKey(_workspacePathKey);
				else
					EditorPrefs.SetString(_workspacePathKey, newValue);
				_workspacePath = newValue;
				UpdateWorkspacePath();
			}
			GUILayout.EndHorizontal();
		}

		private string GetCodeWorkspaceSolution(string solution)
		{
			if (_workspaceAbsolutePath != null)
				return _workspacePath;
			return solution;
		}
	}
}