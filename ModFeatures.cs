using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;
using UnityEngine.Video;
using Verse;

namespace Brrainz
{
	public static class ModFeatures
	{
		const string modFeatureId = "brrainz.mod.features";
		static Queue<Type> mods = new();
		static bool showNextDialog = false;

		static void Root_Update_Postfix()
		{
			if (mods == null || showNextDialog == false)
				return;
			if (mods.Count == 0)
			{
				mods = null;
				return;
			}
			var type = mods.Dequeue();
			var dialog = new Dialog_ModFeatures(type, () => showNextDialog = true, false);
			if (dialog.TopicCount > 0)
				Find.WindowStack.Add(dialog);
			showNextDialog = false;
		}

		static void Game_FinalizeInit_Postfix() => showNextDialog = true;

		public static void Install<T>() where T : Mod
		{
			var harmony = new Harmony(modFeatureId);
			ReadOnlyCollection<string> owners;

			var m_Root_Update = SymbolExtensions.GetMethodInfo((Root root) => root.Update());
			var m_Root_Update_Postfix = SymbolExtensions.GetMethodInfo(() => Root_Update_Postfix());
			owners = Harmony.GetPatchInfo(m_Root_Update)?.Owners;
			if (owners == null || owners.Contains(modFeatureId) == false)
				harmony.Patch(m_Root_Update, null, postfix: new HarmonyMethod(m_Root_Update_Postfix));

			var m_Game_FinalizeInit = SymbolExtensions.GetMethodInfo((Game game) => game.FinalizeInit());
			var m_Game_FinalizeInit_Postfix = SymbolExtensions.GetMethodInfo(() => Game_FinalizeInit_Postfix());
			owners = Harmony.GetPatchInfo(m_Game_FinalizeInit)?.Owners;
			if (owners == null || owners.Contains(modFeatureId) == false)
				harmony.Patch(m_Game_FinalizeInit, null, postfix: new HarmonyMethod(m_Game_FinalizeInit_Postfix));

			mods.Enqueue(typeof(T));
		}

		public static int UnseenFeatures<T>() where T : Mod
		{
			var type = typeof(T);
			var dialog = new Dialog_ModFeatures(type, null, false);
			return dialog.TopicCount;
		}

		public static void ShowAgain<T>(bool showAll) where T : Mod
		{
			var type = typeof(T);
			var dialog = new Dialog_ModFeatures(type, null, showAll);
			Find.WindowStack.Add(dialog);
		}
	}

	[StaticConstructorOnStartup]
	internal class Dialog_ModFeatures : Window
	{
		const float videoWidth = 640;
		const float videoHeight = 480;
		const float margin = 20;
		const float buttonSpacing = 4;
		const float rowHeightFactor = 2f;
		const int topicCountBeforeScroll = 9;
		const int trashScalerStart = 300;
		const int trashScalerPeriod = 240;

		[DataContract]
		class Configuration
		{
			[DataMember] string[] Dismissed { get; set; } = [];

			internal bool IsDismissed(string topic) => Dismissed.Contains(topic);

			internal void MarkDismissed(string topic, Action saveCallback)
			{
				if (IsDismissed(topic) == false)
				{
					Dismissed = [.. Dismissed, .. new[] { topic }];
					saveCallback();
				}
			}
		}

		Vector2 scrollPosition;
		Texture currentTexture;
		RenderTexture renderTexture;
		float titleHeight, textHeight;
		VideoPlayer videoPlayer;

		readonly string modName;
		readonly Action closeCallback;
		readonly bool showAll;
		readonly string configurationPath;
		readonly string resourceDir;

		static readonly Texture2D[] frameColors = [
			SolidColorMaterials.NewSolidColorTexture(Color.yellow.ToTransparent(0.2f)),
			SolidColorMaterials.NewSolidColorTexture(Color.yellow.ToTransparent(0.3f)),
			SolidColorMaterials.NewSolidColorTexture(Color.white.ToTransparent(0.3f)),
			SolidColorMaterials.NewSolidColorTexture(Color.white.ToTransparent(0.4f))
		];
		static readonly Color[] bgColors = [Color.yellow.ToTransparent(0.05f), Color.yellow.ToTransparent(0.1f), Color.white.ToTransparent(0.15f), Color.white.ToTransparent(0.2f)];

		int selected = -1;
		string title = "";
		Configuration configuration = new();
		string[] topicResources;
		Texture2D[] topicTextures;
		float listWidthCached = -1;
		int ticks;

		string TopicTranslated(int i) => $"Feature_{modName}_{topicResources[i].Substring(3).Replace(".png", "").Replace(".mp4", "")}".Translate();
		string TopicType(int i) => topicResources[i].EndsWith(".png") ? "image" : "video";
		string TopicPath(int i) => $"{resourceDir}{Path.DirectorySeparatorChar}{topicResources[i]}";
		bool HasScrollbar => topicResources.Length > topicCountBeforeScroll;
		float ListWidth
		{
			get
			{
				if (listWidthCached < 0)
				{
					Text.Font = GameFont.Small;
					for (var i = 0; i < topicResources.Length; i++)
					{
						var width = Text.CalcSize(TopicTranslated(i)).x;
						listWidthCached = Mathf.Max(listWidthCached, width);
					}
					listWidthCached += margin * 2 + buttonSpacing + textHeight * rowHeightFactor;
					if (HasScrollbar)
						listWidthCached += 20;
				}
				return listWidthCached;
			}
		}
		public override Vector2 InitialSize => new(ListWidth + videoWidth + margin * 3, videoHeight + titleHeight + margin * 3);

		internal Dialog_ModFeatures(Type type, Action closeCallback, bool showAll)
		{
			doCloseX = true;
			forcePause = true;
			absorbInputAroundWindow = true;
			silenceAmbientSound = true;
			closeOnClickedOutside = true;

			modName = type.Name;
			this.closeCallback = closeCallback;
			this.showAll = showAll;
			ticks = -trashScalerStart;

			var modContentPack = LoadedModManager.RunningMods.FirstOrDefault(mod => mod.assemblies.loadedAssemblies.Contains(type.Assembly));
			var rootDir = (modContentPack?.RootDir) ?? throw new Exception($"Could not find root mod directory for {type.Assembly.FullName}");
			resourceDir = $"{rootDir}{Path.DirectorySeparatorChar}Features";
			var folderPath = Path.Combine(GenFilePaths.ConfigFolderPath, "ModFeatures");
			if (Directory.Exists(folderPath) == false)
				Directory.CreateDirectory(folderPath);
			var filename = GenText.SanitizeFilename(string.Format("{0}_{1}.json", modContentPack.FolderName, modName));
			configurationPath = Path.Combine(folderPath, filename);

			Load();
			ReloadTopicsAndTextures();
		}

		public void ReloadTopicsAndTextures()
		{
			topicResources = Directory.GetFiles(resourceDir)
				.Select(f => Path.GetFileName(f))
				.OrderBy(f => f)
				.Where(topic => showAll || configuration.IsDismissed(topic) == false)
				.ToArray();
			topicTextures = new Texture2D[topicResources.Length];
		}

		public void Load()
		{
			try
			{
				if (File.Exists(configurationPath))
				{
					var serializer = new DataContractJsonSerializer(typeof(Configuration));
					using var stream = new FileStream(configurationPath, FileMode.Open);
					configuration = (Configuration)serializer.ReadObject(stream);
					return;
				}
			}
			catch
			{
			}
			configuration = new Configuration();
		}

		public void Save()
		{
			try
			{
				var serializer = new DataContractJsonSerializer(typeof(Configuration));
				using var stream = new FileStream(configurationPath, FileMode.OpenOrCreate);
				serializer.WriteObject(stream, configuration);
			}
			finally
			{
			}
		}

		public override float Margin => margin;
		internal int TopicCount => topicResources.Length;

		public override void PreOpen()
		{
			Text.Font = GameFont.Medium;
			titleHeight = Text.CalcHeight(title, 10000);
			Text.Font = GameFont.Small;
			textHeight = Text.CalcHeight("#", 10000);
			renderTexture = new RenderTexture((int)videoWidth, (int)videoHeight, 24, RenderTextureFormat.ARGB32);
			videoPlayer = Find.Camera.gameObject.AddComponent<VideoPlayer>();
			videoPlayer = Find.Root.gameObject.AddComponent<VideoPlayer>();
			videoPlayer.playOnAwake = false;
			videoPlayer.renderMode = VideoRenderMode.RenderTexture;
			videoPlayer.waitForFirstFrame = true;
			videoPlayer.aspectRatio = VideoAspectRatio.FitInside;
			videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
			videoPlayer.targetTexture = renderTexture;
			ShowTopic(0);
			base.PreOpen();
		}

		public override void PreClose()
		{
			videoPlayer.Stop();
			videoPlayer.targetTexture = null;
			base.PreClose();
			UnityEngine.Object.DestroyImmediate(videoPlayer, true);
			renderTexture.Release();
		}

		public override void PostClose()
		{
			base.PostClose();
			closeCallback?.Invoke();
		}

		public void ShowTopic(int i)
		{
			var path = TopicPath(i);
			title = TopicTranslated(i);
			selected = i;

			if (TopicType(i) == "image")
			{
				videoPlayer.Stop();
				if (topicTextures[i] == null)
				{
					topicTextures[i] = new Texture2D(1, 1, TextureFormat.ARGB32, false);
					topicTextures[i].LoadImage(File.ReadAllBytes(path));
				}
				currentTexture = topicTextures[i];
				return;
			}

			RenderTexture.active = renderTexture;
			GL.Clear(true, true, Color.black);
			RenderTexture.active = null;

			videoPlayer.Stop();
			videoPlayer.url = path;
			videoPlayer.frame = 0;
			videoPlayer.Play();
			currentTexture = renderTexture;
		}

		public float TrashScaler(int idx)
		{
			ticks++;
			if (ticks < 0)
				return 4f;
			var start = trashScalerPeriod * idx;
			var f = GenMath.LerpDoubleClamped(start, start + 2 * trashScalerPeriod, 0, 2f, ticks);
			if (f < 1f)
				return 4f + 4 * f;
			else
				return 4f + 4 * (2 - f);
		}

		public override void DoWindowContents(Rect inRect)
		{
			var font = Text.Font;
			var titleRect = new Rect(ListWidth + margin, 0f, inRect.width - ListWidth - margin, titleHeight);
			Text.Font = GameFont.Medium;
			Widgets.Label(titleRect, title);
			Text.Font = GameFont.Small;

			var rowHeight = textHeight * rowHeightFactor;
			var rowSpacing = rowHeight / 2.7f;
			var viewRect = new Rect(0f, 0f, ListWidth - (HasScrollbar ? 20 : 0), (rowHeight + rowSpacing) * topicResources.Length - rowSpacing);
			Widgets.BeginScrollView(new Rect(0f, 0f, ListWidth, inRect.height), ref scrollPosition, viewRect, true);
			for (var i = 0; i < topicResources.Length; i++)
			{
				var r1 = new Rect(0f, (rowHeight + rowSpacing) * i, viewRect.width - (showAll ? 0 : buttonSpacing + rowHeight), rowHeight);

				var hover = Mouse.IsOver(r1) ? 1 : 0;
				Widgets.DrawBoxSolid(r1, bgColors[hover + (selected == i ? 2 : 0)]);
				Widgets.DrawBox(r1, 1, frameColors[hover + (selected == i ? 2 : 0)]);
				var anchor = Text.Anchor;
				Text.Anchor = TextAnchor.MiddleLeft;
				var labelRect = r1;
				labelRect.x += margin;
				labelRect.width -= 2 * margin;
				Widgets.Label(labelRect, TopicTranslated(i));
				Text.Anchor = anchor;
				if (Widgets.ButtonInvisible(r1))
					ShowTopic(i);

				if (showAll == false)
				{
					var r2 = new Rect(r1.xMax + buttonSpacing, (rowHeight + rowSpacing) * i, rowHeight, rowHeight);

					if (Widgets.ButtonInvisible(r2))
					{
						configuration.MarkDismissed(topicResources[i], () => Save());
						currentTexture = null;
						title = "";
						selected = -1;
						ReloadTopicsAndTextures();
						if (TopicCount == 0)
							Close();
					}

					hover = Mouse.IsOver(r2) ? 1 : 0;
					Widgets.DrawBoxSolid(r2, bgColors[hover + (selected == i ? 2 : 0)]);
					Widgets.DrawBox(r2, 1, frameColors[hover + (selected == i ? 2 : 0)]);
					r2 = r2.ExpandedBy(-rowHeight / TrashScaler(i));
					Widgets.DrawTextureFitted(r2, MainTabWindow_Quests.DismissIcon, 1f);
				}
			}
			Widgets.EndScrollView();

			if (currentTexture != null)
			{
				var previewRect = new Rect(ListWidth + margin, titleHeight + margin, videoWidth, videoHeight);
				Widgets.DrawBoxSolid(previewRect, Color.black);
				GUI.DrawTexture(previewRect, currentTexture);
			}

			Text.Font = font;
		}
	}
}
