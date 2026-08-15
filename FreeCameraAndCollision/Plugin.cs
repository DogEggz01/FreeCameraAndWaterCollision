using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace FreeCameraAndCollision
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public sealed class FreeCameraAndCollisionPlugin : BaseUnityPlugin
	{
		public const string PluginGuid =
			"com.DogEggz.sailwind.freecameraandcollision";

		public const string PluginName = "FreeCamera and Collision";

		public const string PluginVersion = "1.2.0";

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
				Logger.LogInfo("Removed obsolete solid-collision configuration.");
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
					new ConfigurationManagerMetadata
					{
						ShowRangeAsPercent = false,
						Order = order
					}));
		}
	}

	internal sealed class ConfigurationManagerMetadata
	{
		public bool? ShowRangeAsPercent;

		public int? Order;
	}
}
