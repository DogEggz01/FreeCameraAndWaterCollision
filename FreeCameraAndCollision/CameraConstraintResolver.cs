using Crest;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace FreeCameraAndCollision
{
	internal sealed class CameraConstraintResolver
	{
		private readonly FreeCameraAndCollisionPlugin plugin;
		private readonly UnityObject queryContext;
		private readonly SampleHeightHelper waterHeightSampler =
			new SampleHeightHelper();

		internal CameraConstraintResolver(
			FreeCameraAndCollisionPlugin ownerPlugin,
			UnityObject owner)
		{
			plugin = ownerPlugin;
			queryContext = owner;
		}

		internal Vector3 ConstrainFreeCameraPosition(
			Vector3 position,
			Vector3 focus,
			float maximumDistance)
		{
			position = ClampToMaximumDistance(position, focus, maximumDistance);
			position = ApplyWaterCollision(position);

			Vector3 offset = position - focus;
			float maximumDistanceSquared = maximumDistance * maximumDistance;
			if (offset.sqrMagnitude <= maximumDistanceSquared)
			{
				return position;
			}

			float verticalOffset = position.y - focus.y;
			if (Mathf.Abs(verticalOffset) >= maximumDistance)
			{
				return focus +
					Vector3.up * Mathf.Sign(verticalOffset) * maximumDistance;
			}

			Vector3 horizontalOffset =
				new Vector3(offset.x, 0f, offset.z);
			float maximumHorizontalDistance = Mathf.Sqrt(
				maximumDistanceSquared - verticalOffset * verticalOffset);

			if (horizontalOffset.sqrMagnitude >
				maximumHorizontalDistance * maximumHorizontalDistance)
			{
				horizontalOffset =
					horizontalOffset.normalized * maximumHorizontalDistance;
			}

			return focus + horizontalOffset + Vector3.up * verticalOffset;
		}

		private Vector3 ClampToMaximumDistance(
			Vector3 position,
			Vector3 focus,
			float maximumDistance)
		{
			Vector3 offset = position - focus;
			if (offset.sqrMagnitude <= maximumDistance * maximumDistance)
			{
				return position;
			}

			return focus + offset.normalized * maximumDistance;
		}

		internal Vector3 ApplyWaterCollision(Vector3 position)
		{
			if (!plugin.WaterCollision.Value || OceanRenderer.Instance == null)
			{
				return position;
			}

			waterHeightSampler.Init(
				position,
				plugin.SphereRadius.Value * 2f,
				true,
				queryContext);

			if (!waterHeightSampler.Sample(out float waterHeight))
			{
				return position;
			}

			float minimumHeight =
				waterHeight +
					plugin.SphereRadius.Value +
					plugin.SurfacePadding.Value;

			if (position.y < minimumHeight)
			{
				position.y = minimumHeight;
			}

			return position;
		}
	}
}
