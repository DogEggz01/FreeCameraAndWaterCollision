using HarmonyLib;

namespace FreeCameraAndCollision
{
	internal static class GameInputSuppression
	{
		internal static bool AllowOriginalOrNeutralize(ref bool result)
		{
			if (!FreeCameraController.IsInputCaptured)
			{
				return true;
			}

			result = false;
			return false;
		}

		internal static bool AllowOriginalOrNeutralize(ref float result)
		{
			if (!FreeCameraController.IsInputCaptured)
			{
				return true;
			}

			result = 0f;
			return false;
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetKey")]
	internal static class GameInputGetKeyPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref bool __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetKeyDown")]
	internal static class GameInputGetKeyDownPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref bool __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetKeyUp")]
	internal static class GameInputGetKeyUpPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref bool __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetScrollAxis")]
	internal static class GameInputGetScrollAxisPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref float __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetPrimaryHorizontal")]
	internal static class GameInputGetPrimaryHorizontalPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref float __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetPrimaryVertical")]
	internal static class GameInputGetPrimaryVerticalPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref float __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetSecondaryHorizontal")]
	internal static class GameInputGetSecondaryHorizontalPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref float __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}

	[HarmonyPatch(typeof(GameInput), "GetSecondaryVertical")]
	internal static class GameInputGetSecondaryVerticalPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref float __result)
		{
			return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
		}
	}
}
