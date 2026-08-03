using System.Collections.Generic;
using System.Linq;
using MultiplayerSFS.Common;
using SFS.Parts;
using SFS.World;

namespace MultiplayerSFS.Mod;

public class LocalRocket
{
	public Rocket rocket;

	public Dictionary<int, Part> parts;

	public Interpolator interpolator;

	public LocalRocket(Rocket rocket)
	{
		this.rocket = rocket;
		parts = new Dictionary<int, Part>(rocket.partHolder.partsSet.Count);
		foreach (Part item in rocket.partHolder.partsSet)
		{
			parts.InsertNew(item);
		}
		interpolator = rocket.gameObject.AddComponent<Interpolator>();
		interpolator.rocket = this;
	}

	public LocalRocket(Rocket rocket, Dictionary<int, Part> parts)
	{
		this.rocket = rocket;
		this.parts = parts;
		interpolator = rocket.gameObject.AddComponent<Interpolator>();
		interpolator.rocket = this;
	}

	public RocketState ToState()
	{
		return new RocketState
		{
			rocketName = rocket.rocketName,
			location = rocket.location.Value.ToNetLocation(),
			rotation = rocket.rb2d.transform.eulerAngles.z,
			angularVelocity = rocket.rb2d.angularVelocity,
			throttleOn = rocket.throttle.throttleOn,
			throttlePercent = rocket.throttle.throttlePercent,
			RCS = rocket.arrowkeys.rcs,
			input_Turn = rocket.arrowkeys.turnAxis,
			input_Raw = rocket.arrowkeys.rawArrowkeysAxis,
			input_Horizontal = rocket.arrowkeys.horizontalAxis,
			input_Vertical = rocket.arrowkeys.verticalAxis,
			parts = parts.Where((KeyValuePair<int, Part> kvp) => kvp.Value != null).ToDictionary((KeyValuePair<int, Part> kvp) => kvp.Key, (KeyValuePair<int, Part> kvp) => new PartState(new PartSave(kvp.Value))),
			joints = (from pj in rocket.jointsGroup.joints
				where pj.a != null && pj.b != null
				select new JointState(GetPartID(pj.a), GetPartID(pj.b))).ToList(),
			stages = rocket.staging.stages.Select((Stage s) => new StageState(s.stageId, s.parts.Where((Part p) => p != null).Select(GetPartID).ToList())).ToList()
		};
	}

	public int GetPartID(Part part)
	{
		KeyValuePair<int, Part> keyValuePair = parts.FirstOrDefault((KeyValuePair<int, Part> p) => p.Value == part);
		if (keyValuePair.Value != null)
		{
			return keyValuePair.Key;
		}
		return -1;
	}
}
