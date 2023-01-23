# Fork of com.unity.ide.visualstudio

Add advanced filters aiming to improve the developer experience when working with Visual Studio and Unity.

## Rationale
The more packages or assembly definitions you have in your project the more sluggish your Visual Studio experience becomes.
This is most notably on startup but also when attaching Visual Studio to e.g. trigger break points.

With this fork you can select exactly what source code you want to be able to see (and step through and debug) in visual studio and reduce the generated vs projects to just the few you actually need:

Consider this simplified example:

![image](https://user-images.githubusercontent.com/3404365/192978093-41356aed-2333-4dbe-9aad-95e371720f31.png)

turns into:

![image](https://user-images.githubusercontent.com/3404365/192978279-5caf95cd-7a1e-4fe0-bc93-8fd05cc89e2d.png)

By that you're able to remove all those projects from your Visual Studio solution you'd never look at anyway or even edit.

## Installation
You just replace the original package with this fork inside the `manifest.json`:

In the dependencies find the line 

```
"com.unity.ide.visualstudio": "2.0.17",
```

and replace it with

```
"com.unity.ide.visualstudio": "https://github.com/krisrok/com.unity.ide.visualstudio.git#2.0.17-advanced_package_filter",
```

(also works for 2.0.16)

## Usage

1. Navigate to Edit/Preferences/External Tools.
2. Use the the pre-existing filters to choose between embedded, local packages and so on. (Note this functionality is already provided by Unity's original package.)
3. Now, for finer-grained control you can open the the "Advanced Filters" foldout:
4. Select which packages and assemblies (defined by .asmdefs) you actually want in your solution. Tip: You can also use Ctrl/Shift to add/remove checkmarks in bulk while moving your cursor over them.
5. Click "Regenerate project files"
6. Open Visual Studio e.g. by opening a script. If the solution is already open Visual Studio should display a dialog to automatically re-open it.

## Note
This is a fork of a mirror package because Unity does not provide public access to the original sources.

[Mirrored from UPM, not affiliated with Unity Technologies.] 📦 Code editor integration for supporting Visual Studio as code editor for unity. Adds support for generating csproj files for intellisense purposes, auto discovery of installations, etc.
