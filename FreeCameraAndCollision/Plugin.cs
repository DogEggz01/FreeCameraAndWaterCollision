using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FreeCameraAndCollision
{
	internal sealed class ConfigurationManagerAttributes
	{
		public bool? Browsable;
		public bool? HideDefaultButton;
		public bool? ShowRangeAsPercent;
		public int? Order;
		public Action<ConfigEntryBase> CustomDrawer;
	}

	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public sealed class FreeCameraAndCollisionPlugin : BaseUnityPlugin
	{
		public const string PluginGuid =
			"com.DogEggz.sailwind.freecameraandcollision";

		public const string PluginName = "FreeCamera and Collision";

		public const string PluginVersion = "1.3.1";

		private static readonly GUIContent WorldMovementButton =
			new GUIContent(
				"World",
				"Camera movement related to game world.");

		private static readonly GUIContent DroneMovementButton =
			new GUIContent(
				"Drone",
				"Camera movement based on camera axis.");

		private static readonly GUIContent InvertPitchToggle =
			new GUIContent(
				"Invert Mouse Pitch",
				"Invert vertical mouse input in Drone mode only.");

		private static readonly ConfigDefinition[] ObsoleteConfigDefinitions =
		{
			new ConfigDefinition("Collision", "Collision Enabled"),
			new ConfigDefinition("Collision", "Solid Collision"),
			new ConfigDefinition("Collision Tuning", "Return Smooth Time")
		};

		private static readonly PropertyInfo OrphanedEntriesProperty =
			AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");

		private Harmony harmony;

		internal static FreeCameraAndCollisionPlugin Instance { get; private set; }

		internal ConfigEntry<bool> WaterCollision { get; private set; }

		internal ConfigEntry<float> SphereRadius { get; private set; }

		internal ConfigEntry<float> SurfacePadding { get; private set; }

		internal ConfigEntry<FreeCameraMovementMode> MovementMode
		{
			get;
			private set;
		}

		internal ConfigEntry<int> CameraSpeed { get; private set; }

		internal ConfigEntry<int> CameraRollingSpeed { get; private set; }

		internal ConfigEntry<bool> InvertDroneMousePitch { get; private set; }

		private void Awake()
		{
			Instance = this;

			BindConfiguration();
			RemoveObsoleteConfiguration();

			harmony = new Harmony(PluginGuid);
			harmony.PatchAll(Assembly.GetExecutingAssembly());

			if (BoatCamera.instance != null)
			{
				AttachController(BoatCamera.instance);
			}

			Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
		}

		private void OnDestroy()
		{
			FreeCameraController.ExitActiveCamera();
			harmony?.UnpatchSelf();

			if (Instance == this)
			{
				Instance = null;
			}
		}

		internal void AttachController(BoatCamera boatCamera)
		{
			if (boatCamera == null)
			{
				return;
			}

			FreeCameraController controller =
				boatCamera.GetComponent<FreeCameraController>() ??
				boatCamera.gameObject.AddComponent<FreeCameraController>();

			controller.Initialize(boatCamera, this);
		}

		internal void LogInitializationError(string message)
		{
			Logger.LogError(message);
		}

		private void BindConfiguration()
		{
			MovementMode = Config.Bind(
				"Free Camera",
				"Movement Mode",
				FreeCameraMovementMode.World,
				new ConfigDescription(
					"Select how free-camera movement is oriented.",
					null,
					new ConfigurationManagerAttributes
					{
						Order = 10,
						HideDefaultButton = true,
						CustomDrawer = DrawMovementModeButtons
					}));

			CameraSpeed = BindIntegerSlider(
				"Free Camera",
				"Camera Speed",
				10,
				1,
				40,
				"Free-camera positional movement speed. Shift doubles it and Alt halves it. Drone acceleration and stopping use a two-second response; Shift/Alt speed changes use one second.",
				20,
				true);

			CameraRollingSpeed = BindIntegerSlider(
				"Free Camera",
				"Camera Rolling Speed",
				60,
				10,
				120,
				"Q/E rolling speed in degrees per second. This is not affected by Camera Speed, Shift, or Alt.",
				30,
				false);

			InvertDroneMousePitch = Config.Bind(
				"Drone Mode",
				"Invert Mouse Pitch",
				false,
				new ConfigDescription(
					"Invert vertical mouse input in Drone mode only.",
					null,
					new ConfigurationManagerAttributes
					{
						Browsable = false
					}));

			WaterCollision = Config.Bind(
				"Collision",
				"Water Collision",
				true,
				"Keep the camera sphere above the animated Crest water surface. Changes apply immediately.");

			SphereRadius = BindSlider(
				"Collision Tuning",
				"Sphere Radius",
				0.3f,
				0.05f,
				1.5f,
				"Radius used to sample and stay above the animated water surface.",
				30);

			SurfacePadding = BindSlider(
				"Collision Tuning",
				"Surface Padding",
				0.1f,
				0f,
				0.5f,
				"Additional height kept between the camera sphere and the water surface.",
				20);
		}

		private void RemoveObsoleteConfiguration()
		{
			try
			{
				IDictionary<ConfigDefinition, string> orphanedEntries =
					OrphanedEntriesProperty?.GetValue(Config, null) as
						IDictionary<ConfigDefinition, string>;

				if (orphanedEntries == null)
				{
					Logger.LogWarning(
						"Could not inspect obsolete configuration entries.");
					return;
				}

				bool removedAny = false;
				for (int i = 0; i < ObsoleteConfigDefinitions.Length; i++)
				{
					removedAny |=
						orphanedEntries.Remove(ObsoleteConfigDefinitions[i]);
				}

				if (removedAny)
				{
					Config.Save();
					Logger.LogInfo(
						"Removed obsolete solid-collision configuration.");
				}
			}
			catch (Exception exception)
			{
				Logger.LogWarning(
					$"Could not remove obsolete configuration entries: {exception.GetBaseException().Message}");
			}
		}

		private ConfigEntry<float> BindSlider(
			string section,
			string key,
			float defaultValue,
			float minimum,
			float maximum,
			string description,
			int order)
		{
			return Config.Bind(
				section,
				key,
				defaultValue,
				new ConfigDescription(
					description,
					new AcceptableValueRange<float>(minimum, maximum),
					new ConfigurationManagerAttributes
					{
						ShowRangeAsPercent = false,
						Order = order
					}));
		}

		private ConfigEntry<int> BindIntegerSlider(
			string section,
			string key,
			int defaultValue,
			int minimum,
			int maximum,
			string description,
			int order,
			bool showDefaultButton)
		{
			return Config.Bind(
				section,
				key,
				defaultValue,
				new ConfigDescription(
					description,
					new AcceptableValueRange<int>(minimum, maximum),
					new ConfigurationManagerAttributes
					{
						ShowRangeAsPercent = false,
						HideDefaultButton = !showDefaultButton,
						Order = order
					}));
		}

		private static void DrawMovementModeButtons(ConfigEntryBase setting)
		{
			ConfigEntry<FreeCameraMovementMode> movementMode =
				setting as ConfigEntry<FreeCameraMovementMode>;
			if (movementMode == null)
			{
				return;
			}

			bool wasEnabled = GUI.enabled;
			GUILayout.BeginVertical();
			GUILayout.BeginHorizontal();

			GUI.enabled =
				wasEnabled && movementMode.Value > FreeCameraMovementMode.World;
			if (GUILayout.Button(
				WorldMovementButton,
				GUILayout.ExpandWidth(true)))
			{
				movementMode.Value = FreeCameraMovementMode.World;
			}

			GUI.enabled =
				wasEnabled && movementMode.Value != FreeCameraMovementMode.Drone;
			if (GUILayout.Button(
				DroneMovementButton,
				GUILayout.ExpandWidth(true)))
			{
				movementMode.Value = FreeCameraMovementMode.Drone;
			}

			GUILayout.EndHorizontal();
			GUI.enabled = wasEnabled;

			if (movementMode.Value == FreeCameraMovementMode.Drone &&
				Instance?.InvertDroneMousePitch != null)
			{
				bool currentValue = Instance.InvertDroneMousePitch.Value;
				bool nextValue = GUILayout.Toggle(
					currentValue,
					InvertPitchToggle,
					GUILayout.ExpandWidth(true));

				if (nextValue != currentValue)
				{
					Instance.InvertDroneMousePitch.Value = nextValue;
				}
			}

			GUILayout.EndVertical();
		}
	}
}
