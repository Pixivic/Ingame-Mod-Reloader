using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ModReloader;

internal static class ReloadSession
{
    private const string ResumeKey = ModIdentity.Name + ".PendingResume";
    private const string LoadScreen = "Terraria.ModLoader.UI.UILoadMods";
    private const int LoadingWorldMenu = 10;
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo SuccessfulLoad = typeof(ModLoader).GetField("OnSuccessfulLoad", StaticMembers);
    private static readonly MethodInfo Reload = typeof(ModLoader).GetMethod("Reload", StaticMembers,
        null, Type.EmptyTypes, null);
    private static bool _busy;

    public static bool Busy => _busy;

    public static bool Start(string modName)
    {
        if (_busy || Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer
            || modName is "ModLoader" or ModIdentity.Name || !ModLoader.TryGetMod(modName, out Mod mod))
            return false;

        try
        {
            if (Reload?.ReturnType != typeof(void) || SuccessfulLoad?.FieldType != typeof(Action))
                throw new NotSupportedException("This tModLoader build does not expose the required reload APIs.");
            if (SuccessfulLoad?.GetValue(null) != null)
                throw new InvalidOperationException("tModLoader already has a pending reload action.");

            string source = FindSource(mod);
            SourceBuild build = source == null ? null : GetSourceBuild(source);

            var player = Main.ActivePlayerFileData;
            var world = Main.ActiveWorldFileData;
            if (string.IsNullOrEmpty(player?.Path) || string.IsNullOrEmpty(world?.Path))
                return false;

            var resume = new ResumeRequest((player.Path, player.IsCloudSave, world.Path, world.IsCloudSave),
                ModLoader.Mods.Select(entry => entry.Name).ToArray());

            _busy = true;
            Logger.Info($"Saving before reload of {modName}.");
            WorldGen.SaveAndQuit(() => Main.QueueMainThreadAction(() => PrepareReload(resume, build)));
            return true;
        }
        catch (Exception error)
        {
            _busy = false;
            Logger.Error("Could not start reload.", error);
            return false;
        }
    }

    private static void PrepareReload(ResumeRequest resume, SourceBuild build)
    {
        if (!Main.gameMenu || Main.menuMode != 0)
        {
            _busy = false;
            return;
        }

        if (build == null)
        {
            BeginReload(resume);
            return;
        }

        Main.menuMode = LoadingWorldMenu;
        Main.statusText = $"Building {Path.GetFileName(build.Path)}...";
        _ = Task.Run(() =>
        {
            try
            {
                build.UpdateReferencesFolder.Invoke(null, null);
                object reporter = Activator.CreateInstance(build.ReporterType, nonPublic: true);
                object compiler = build.CompilerConstructor.Invoke(new[] { reporter });
                build.BuildMethod.Invoke(compiler, new object[] { build.Path });
                Main.QueueMainThreadAction(() => BeginReload(resume));
            }
            catch (Exception error)
            {
                Main.QueueMainThreadAction(() => FailAtMenu("Mod build failed. Select the saved world manually.",
                    error is TargetInvocationException { InnerException: { } cause } ? cause : error));
            }
        });
    }

    private static string FindSource(Mod mod)
    {
        string recorded = mod.SourceFolder;
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            recorded = Path.TrimEndingDirectorySeparator(recorded);
            if (string.Equals(Path.GetFileName(recorded), mod.Name, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(recorded, "build.txt")))
                return recorded;
        }

        string conventional = Path.Combine(Terraria.Program.SavePathShared, "ModSources", mod.Name);
        return File.Exists(Path.Combine(conventional, "build.txt")) ? conventional : null;
    }

    private static SourceBuild GetSourceBuild(string path)
    {
        Type compiler = typeof(ModLoader).Assembly.GetType("Terraria.ModLoader.Core.ModCompile");
        Type reporter = compiler?.GetNestedType("ConsoleBuildStatus", BindingFlags.NonPublic);
        Type status = compiler?.GetNestedType("IBuildStatus", BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo update = compiler?.GetMethod("UpdateReferencesFolder", StaticMembers, null, Type.EmptyTypes, null);
        MethodInfo build = compiler?.GetMethod("Build", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(string) }, null);
        ConstructorInfo ctor = status == null ? null : compiler.GetConstructor(new[] { status });

        if (reporter == null || ctor == null || update?.ReturnType != typeof(void) || build?.ReturnType != typeof(void))
            throw new NotSupportedException("This tModLoader build does not expose the required mod compiler APIs.");

        return new SourceBuild(path, reporter, update, ctor, build);
    }

    private static void BeginReload(ResumeRequest resume)
    {
        Action marker = null;
        try
        {
            if (!Main.gameMenu || (Main.menuMode != 0 && Main.menuMode != LoadingWorldMenu)
                || SuccessfulLoad?.GetValue(null) != null)
                throw new InvalidOperationException("The active menu changed before the reload could start.");

            marker = new CancellationTokenSource().Cancel;
            AppDomain.CurrentDomain.SetData(ResumeKey, (resume.Saves, resume.LoadedMods, marker));
            SuccessfulLoad.SetValue(null, marker);

            Logger.Info("Save finished. Reloading the active mod set.");
            Reload.Invoke(null, null);
        }
        catch (Exception error)
        {
            if (marker != null)
            {
                if (Equals(SuccessfulLoad.GetValue(null), marker))
                    SuccessfulLoad.SetValue(null, null);
                AppDomain.CurrentDomain.SetData(ResumeKey, null);
            }
            FailAtMenu("Mod reload failed to start. Select the saved world manually.", error);
        }
        finally
        {
            _busy = false;
        }
    }

    public static void AttachResume()
    {
        if (AppDomain.CurrentDomain.GetData(ResumeKey) is not
            ValueTuple<(string Player, bool PlayerCloud, string World, bool WorldCloud), string[], Action> pending)
            return;

        AppDomain.CurrentDomain.SetData(ResumeKey, null);
        var (saves, loadedMods, marker) = pending;
        if (SuccessfulLoad?.GetValue(null) is not Action callbacks || !callbacks.GetInvocationList().Contains(marker))
            return;

        Action resume = () => Main.QueueMainThreadAction(() => Resume(saves, loadedMods));
        SuccessfulLoad.SetValue(null, (callbacks - marker) + resume);
    }

    private static void Resume((string Player, bool PlayerCloud, string World, bool WorldCloud) saves,
        string[] loadedMods)
    {
        if (!Main.gameMenu || Main.menuMultiplayer || Main.netMode != NetmodeID.SinglePlayer
            || (Main.menuMode != 0 && Main.MenuUI.CurrentState?.GetType().FullName != LoadScreen))
            return;

        try
        {
            if (!new HashSet<string>(loadedMods, StringComparer.OrdinalIgnoreCase)
                .SetEquals(ModLoader.Mods.Select(mod => mod.Name)))
                throw new InvalidOperationException("The loaded mod set changed during reload.");

            Main.LoadPlayers();
            var player = Main.PlayerList.FirstOrDefault(data => data.IsCloudSave == saves.PlayerCloud
                && string.Equals(data.Path, saves.Player, StringComparison.Ordinal));
            if (player == null)
                throw new FileNotFoundException("The original character could not be found.", saves.Player);
            if (player.Player is not { loadStatus: 0 } character)
                throw new InvalidDataException("The original character could not be loaded.");

            Main.SelectPlayer(player);
            if (Main.ActivePlayerFileData != player)
                throw new InvalidOperationException("The original character could not be selected.");

            var world = Main.WorldList.FirstOrDefault(data => data.IsCloudSave == saves.WorldCloud
                && string.Equals(data.Path, saves.World, StringComparison.Ordinal));
            if (world == null)
                throw new FileNotFoundException("The original world could not be found.", saves.World);
            if (!world.IsValid || !Main.RegisteredGameModes.TryGetValue(world.GameMode, out var gameMode))
                throw new InvalidDataException("The original world has invalid metadata or an unsupported game mode.");
            if ((character.difficulty == PlayerDifficultyID.Creative) != gameMode.IsJourneyMode)
                throw new InvalidOperationException("The saved character and world have incompatible Journey settings.");
            if (!SystemLoader.CanWorldBePlayed(player, world, out ModSystem rejectingSystem))
                throw new InvalidOperationException(rejectingSystem.WorldCanBePlayedRejectionMessage(player, world));

            world.SetAsActive();
            Main.clrInput();
            Main.menuMode = LoadingWorldMenu;
            Logger.Info("Reload finished. Reopening the saved character and world.");
            WorldGen.playWorld();
        }
        catch (Exception error)
        {
            FailAtMenu("Automatic return failed. Select the saved character and world manually.", error);
        }
    }

    private static void FailAtMenu(string message, Exception error)
    {
        _busy = false;
        if (Main.gameMenu && (Main.menuMode == LoadingWorldMenu
            || Main.MenuUI.CurrentState?.GetType().FullName == LoadScreen))
            Main.menuMode = 0;
        Logger.Error(message, error);
    }

    private static log4net.ILog Logger => ModContent.GetInstance<ReloaderMod>().Logger;

    private sealed record ResumeRequest((string Player, bool PlayerCloud, string World, bool WorldCloud) Saves,
        string[] LoadedMods);

    private sealed record SourceBuild(string Path, Type ReporterType, MethodInfo UpdateReferencesFolder,
        ConstructorInfo CompilerConstructor, MethodInfo BuildMethod);
}
