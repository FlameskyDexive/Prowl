﻿// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using CommandLine;

using Prowl.Editor.Assets;
using Prowl.Editor.Editor.CLI;
using Prowl.Editor.Preferences;
using Prowl.Editor.ProjectSettings;
using Prowl.Editor.Utilities;
using Prowl.Runtime;
using Prowl.Runtime.Cloning;
using Prowl.Runtime.SceneManagement;
using Prowl.Runtime.Utils;

namespace Prowl.Editor;

public static class Program
{
    private static bool IsReloadingExternalAssemblies { get; set; }
    public static void RegisterReloadOfExternalAssemblies() => IsReloadingExternalAssemblies = true;

    private static bool s_createdDefaultWindows;
    private static bool s_opened;


    public static int Main(string[] args)
    {
        return Parser.Default.ParseArguments<CliOpenOptions, CliCreateOptions>(args)
                     .MapResult(
                         (CliOpenOptions options) => Run(options),
                         (CliCreateOptions options) => CreateCommand(options),
                         errs => 1); // error
    }

    private static int CreateCommand(CliCreateOptions options)
    {
        Console.WriteLine("Creating a new project");

        if (options?.ProjectPath is not null && !options.ProjectPath.Exists)
        {
            Project.CreateNew(options.ProjectPath);
        }
        else
        {
            Console.WriteLine("Path is not valid or already exists");
        }

        return 0;
    }

    private static int Run(CliOpenOptions options)
    {
        // set global Culture to invariant
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        Application.Initialize += () =>
        {
            // Editor-specific initialization code
            EditorGuiManager.Initialize();
            ImporterAttribute.GenerateLookUp();

            // Start with the project window open
            new ProjectsWindow();
        };

        Application.Update += () =>
        {
            EngineObject.HandleDestroyed();

            if (!s_opened && options?.ProjectPath is not null && options.ProjectPath.Exists)
            {
                Project.Open(new Project(options.ProjectPath));
                s_opened = true;
            }
            //EditorGui.SetupDock();

            AssetDatabase.InternalUpdate();

            if (PlayMode.Current == PlayMode.Mode.Editing) // Dont recompile scripts unless were in editor mode
                CheckReloadingAssemblies();

            // Editor-specific update code
            if (Project.HasProject)
            {
                if (!s_createdDefaultWindows)
                {
                    s_createdDefaultWindows = true;
                    //new EditorMainMenubar();
                    var console = EditorGuiManager.DockWindowTo(new ConsoleWindow(), null, Docking.DockZone.Center);
                    var assetbrowser = EditorGuiManager.DockWindowTo(new AssetsBrowserWindow(), console, Docking.DockZone.Center);
                    // Add Asset Tree, When we do this AssetBrowser node will subdivide into two children
                    var assettree = EditorGuiManager.DockWindowTo(new AssetsTreeWindow(), assetbrowser, Docking.DockZone.Left, 0.2f);
                    // So for the Inspector we need to use the Child to dock now
                    var inspector = EditorGuiManager.DockWindowTo(new InspectorWindow(), assetbrowser.Child[1], Docking.DockZone.Right, 0.75f);
                    // Now Asset Browser is Subdivided twice,
                    assetbrowser = assetbrowser.Child[1].Child[0];
                    var game = EditorGuiManager.DockWindowTo(new GameWindow(), assetbrowser, Docking.DockZone.Top, 0.65f);
                    var scene = EditorGuiManager.DockWindowTo(new SceneViewWindow(), game, Docking.DockZone.Center);

                    // and finally hierarchy on top of asset tree
                    var hierarchy = EditorGuiManager.DockWindowTo(new HierarchyWindow(), assettree, Docking.DockZone.Top, 0.65f);

                    // new ProjectSettingsWindow();
                    // new PreferencesWindow();
                    // new AssetSelectorWindow(typeof(Texture2D), (guid, fileid) => {  });
                }

                Application.DataPath = Project.Active.ProjectPath;

                if (GeneralPreferences.Instance.LockFPS)
                {
                    Graphics.VSync = false;
                    Screen.FramesPerSecond = GeneralPreferences.Instance.TargetFPS;
                }
                else
                {
                    Graphics.VSync = GeneralPreferences.Instance.VSync;
                    Screen.FramesPerSecond = 0;
                }

                if (Hotkeys.IsHotkeyDown("Undo", new() { Key = Key.Z, Ctrl = true }))
                    UndoRedoManager.Undo();
                else if (Hotkeys.IsHotkeyDown("Redo", new() { Key = Key.Y, Ctrl = true }))
                    UndoRedoManager.Redo();

                if (Hotkeys.IsHotkeyDown("SaveSceneAs", new() { Key = Key.S, Ctrl = true, Shift = true }))
                    EditorGuiManager.SaveSceneAs();
                else if (Hotkeys.IsHotkeyDown("SaveScene", new() { Key = Key.S, Ctrl = true }))
                    EditorGuiManager.SaveScene();

                //Application.IsPlaying = PlayMode.Current == PlayMode.Mode.Playing || PlayMode.Current == PlayMode.Mode.Paused;
                Application.IsPlaying = PlayMode.Current == PlayMode.Mode.Playing;

                try
                {
                    bool hasGameWindow = GameWindow.LastFocused != null && GameWindow.LastFocused.IsAlive;
                    // Push GameWindow's input handler
                    if (hasGameWindow) Input.PushHandler((GameWindow.LastFocused.Target as GameWindow).InputHandler);

                    PlayMode.GameTime.Update();
                    Time.TimeStack.Push(PlayMode.GameTime);
                    SceneManager.Update();
                    Time.TimeStack.Pop();

                    if (hasGameWindow) Input.PopHandler();
                }
                catch (Exception e)
                {
                    Debug.LogError("Scene Update Error: " + e.ToString());
                }
            }
        };

        Application.Render += () =>
        {
            EditorGuiManager.Update();

            Graphics.EndFrame();
        };

        Application.Quitting += () =>
        {

        };

        Application.Run("Prowl Editor", 1920, 1080, new EditorAssetProvider(), true);

        return 0;
    }

    public static void CheckReloadingAssemblies()
    {
        if (IsReloadingExternalAssemblies)
        {
            IsReloadingExternalAssemblies = false;

            if (Project.HasProject)
            {
                SceneManager.StoreScene();
                //SceneManager.Clear(); // SceneManager.Clear has OnAssemblyUnload

                try
                {
                    // Unload External Assemblies
                    AssemblyManager.Unload();

                    Project active = Project.Active;

                    DirectoryInfo temp = active.TempDirectory;
                    DirectoryInfo bin = new DirectoryInfo(Path.Combine(temp.FullName, "bin"));
                    DirectoryInfo project = new DirectoryInfo(Path.Combine(bin.FullName, Project.GameCSProjectName, "Editor"));
                    DirectoryInfo editor = new DirectoryInfo(Path.Combine(bin.FullName, Project.EditorCSProjectName));

                    DirectoryInfo tmpProject = new DirectoryInfo(Path.Combine(temp.FullName, "obj", Project.GameCSProjectName));
                    DirectoryInfo tmpEditor = new DirectoryInfo(Path.Combine(temp.FullName, "obj", Project.EditorCSProjectName));

                    string projectOutputPath = Path.Combine(project.FullName, Project.GameCSProjectName + ".dll");
                    string editorOutputPath = Path.Combine(editor.FullName, Project.EditorCSProjectName + ".dll");

                    // Delete everything under Temp/bin
                    if (bin.Exists)
                        Directory.Delete(bin.FullName, true);

                    bin.Create();

                    DotnetCompileOptions options = new DotnetCompileOptions()
                    {
                        isRelease = false,
                        isSelfContained = false,
                        outputPath = project,
                        tempPath = tmpProject
                    };

                    active.GenerateGameProject();
                    active.CompileGameAssembly(options);
                    Assembly? gameAssembly = AssemblyManager.LoadExternalAssembly(projectOutputPath, true);

                    if (gameAssembly != null)
                    {
                        Debug.Log($"Successfully reloaded project assemblies");

                        options.outputPath = editor;
                        options.tempPath = tmpEditor;

                        active.GenerateEditorProject(gameAssembly);
                        active.CompileEditorAssembly(options);
                        Assembly? editorAssembly = AssemblyManager.LoadExternalAssembly(editorOutputPath, true);

                        if (editorAssembly != null)
                            Debug.Log($"Successfully reloaded editor assemblies");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(new Exception("Error reloading assemblies", e));
                }
                finally
                {
                    AssemblyMethodAttributeBase.FindAll();
                    OnAssemblyLoadAttribute.Invoke();

                    SceneManager.RestoreScene();
                    SceneManager.ClearStoredScene();
                }
            }
            else
            {
                Debug.LogError("Cannot reload assemblies, No project loaded.");
            }
        }
    }
}
