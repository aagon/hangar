//This code is partly based on the code from Extraplanetary Launchpads plugin. ExLaunchPad and Recycler classes.
//Thanks Taniwha, I've learnd many things from your code and from our conversation.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AtHangar
{
	//this module adds the ability to store a vessel in a packed state inside
	public class Hangar : PartModule, IPartCostModifier, IControllableModule
	{
		public enum HangarState { Active, Inactive }

		#region Configuration
		//hangar properties
		[KSPField (isPersistant = false)] public string HangarSpace;
		[KSPField (isPersistant = false)] public bool   UseHangarSpaceMesh = false;
		[KSPField (isPersistant = false)] public string AnimatorID;
		[KSPField (isPersistant = false)] public float  EnergyConsumption = 0.75f;
		[KSPField (isPersistant = false)] public float  VolumePerKerbal = 6.7f; // m^3
		[KSPField (isPersistant = false)] public bool   StaticCrewCapacity = true;
		[KSPField (isPersistant = false)] public bool   NoTransfers = false;
		//vessel spawning
		[KSPField (isPersistant = false)] public float  LaunchHeightOffset;
		[KSPField (isPersistant = false)] public string LaunchTransform;
		[KSPField (isPersistant = false)] public string LaunchVelocity;
		[KSPField (isPersistant = true)]  public bool   LaunchWithPunch = false;
		#endregion

		#region Internals
		//physical properties
		[KSPField (isPersistant = true)]  
		public float base_mass = -1f;
		const float  crew_volume_ratio = 0.3f; //only 30% of the remaining volume may be used for crew (i.e. V*(1-usefull_r)*crew_r)
		float usefull_volume_ratio     = 0.7f; //only 70% of the volume may be used by docking vessels
		public float vessels_mass      = -1f;
		public float vessels_cost      = -1f;
		public float used_volume       = -1f;

		//hangar machinery
		BaseHangarAnimator hangar_gates;
		public AnimatorState gates_state { get { return hangar_gates.State; } }
		public HangarState hangar_state { get; private set; }
		public Metric part_metric { get; private set; }
		public Metric hangar_metric { get; private set; }
		public MeshFilter hangar_space { get; private set; }
		public float used_volume_frac { get { if(hangar_metric.Empty) return 0; return used_volume/hangar_metric.volume; } }
		public VesselResources<Vessel, Part, PartResource> hangarResources { get; private set; }
		public List<ResourceManifest> resourceTransferList = new List<ResourceManifest>();

		//vessels storage
		VesselsPack<StoredVessel> stored_vessels = new VesselsPack<StoredVessel>();
		Dictionary<Guid, MemoryTimer> probed_vessels = new Dictionary<Guid, MemoryTimer>();

		//vessel spawn
		public Vector3 launchVelocity;
		Transform launch_transform;
		Vessel launched_vessel;
		Vector3 deltaV = Vector3.zero;
		bool change_velocity = false;

		//in-editor vessel docking
		static readonly string eLock  = "Hangar.EditHangar";
		static readonly string scLock = "Hangar.LoadShipConstruct";
		static readonly List<string> vessel_dirs = new List<string>{"VAB", "SPH", "../Subassemblies"};
		Rect eWindowPos     = new Rect(Screen.width/2-200, 100, 400, 100);
		Rect neWindowPos    = new Rect(Screen.width/2-200, 100, 400, 50);
		Vector2 scroll_view = Vector2.zero;
		VesselsPack<PackedConstruct> packed_constructs = new VesselsPack<PackedConstruct>();
		CraftBrowser vessel_selector;
		VesselType   vessel_type;
		readonly List<Hangar> ready_cecklist = new List<Hangar>();
		public bool Ready;
		#endregion

		#region GUI
		[KSPField (guiName = "Volume",        guiActiveEditor=true)] public string hangar_v;
		[KSPField (guiName = "Dimensions",    guiActiveEditor=true)] public string hangar_d;
		[KSPField (guiName = "Crew Capacity", guiActiveEditor=true)] public string crew_capacity;
		[KSPField (guiName = "Stored Mass",   guiActiveEditor=true)] public string stored_mass;
		[KSPField (guiName = "Stored Cost",   guiActiveEditor=true)] public string stored_cost;
		[KSPField (guiName = "Hangar Doors",  guiActive = true)] public string doors;
		[KSPField (guiName = "Hangar State",  guiActive = true)] public string state;
		[KSPField (guiName = "Hangar Name",   guiActive = true, guiActiveEditor=true, isPersistant = true)]
		public string HangarName = "_none_";

		//update labels
		IEnumerator<YieldInstruction> UpdateStatus()
		{
			while(true)
			{
				doors = hangar_gates.State.ToString();
				state = hangar_state.ToString();
				yield return new WaitForSeconds(0.5f);
			}
		}

		public override string GetInfo()
		{
			string info = "Energy Cosumption:\n";
			info += string.Format("Hangar: {0}/sec\n", EnergyConsumption);
			var gates = part.Modules.OfType<HangarAnimator>().FirstOrDefault(m => m.AnimatorID == AnimatorID);
			if(gates != null) info += string.Format("Doors: {0}/sec\n", gates.EnergyConsumption);
			return info;
		}
		#endregion
		
		#region For HangarWindow
		public List<StoredVessel> GetVessels() { return stored_vessels.Values; }
		
		public StoredVessel GetVessel(Guid vid)
		{
			StoredVessel sv;
			return stored_vessels.TryGetValue(vid, out sv)? sv : null;
		}
		
		public void UpdateMenus(bool visible)
		{
			Events["HideUI"].active = visible;
			Events["ShowUI"].active = !visible;
		}
		
		[KSPEvent (guiActive = true, guiName = "Hide Controls", active = false)]
		public void HideUI () { HangarWindow.HideGUI (); }

		[KSPEvent (guiActive = true, guiName = "Show Controls", active = false)]
		public void ShowUI () { HangarWindow.ShowGUI (); }
		
		public int numVessels() { return stored_vessels.Count; }

		public bool IsControllable { get { return vessel.IsControllable || part.protoModuleCrew.Count > 0; } }
		#endregion
		
		
		#region Setup
		//all initialization goes here instead of the constructor as documented in Unity API
		void update_resources()
		{ hangarResources = new VesselResources<Vessel, Part, PartResource>(vessel); }

		void build_checklist()
		{
			if(HighLogic.LoadedScene != GameScenes.FLIGHT) return;
			ready_cecklist.Clear();
			foreach(Part p in vessel.parts)
			{
				if(p == part) break;
				ready_cecklist.AddRange(p.Modules.OfType<Hangar>());
			}
		}

		bool other_hangars_ready
		{
			get
			{
				if(ready_cecklist.Count == 0) return true;
				bool ready = true;
				foreach(Hangar h in ready_cecklist)
				{ ready &= h.Ready; if(!ready) break; }
				return ready;
			}
		}
		
		public override void OnAwake()
		{
			base.OnAwake ();
			usefull_volume_ratio = (float)Math.Pow(usefull_volume_ratio, 1/3f);
		}
		
		public override void OnStart(StartState state)
		{
			//base OnStart
			base.OnStart(state);
			if(state == StartState.None) return;
			//set vessel type
			EditorLogic el = EditorLogic.fetch;
			if(el != null) vessel_type = el.editorType == EditorLogic.EditorMode.SPH ? VesselType.SPH : VesselType.VAB;
			//setup hangar name
			if(HangarName == "_none_") HangarName = part.partInfo.title;
			//initialize resources
			if(state != StartState.Editor) update_resources();
			//initialize Animator
			part.force_activate();
			hangar_gates = part.Modules.OfType<BaseHangarAnimator>().FirstOrDefault(m => m.AnimatorID == AnimatorID);
			if(hangar_gates == null)
			{
                hangar_gates = new BaseHangarAnimator();
				Utils.Log("Using BaseHangarAnimator");
			}
			else
            {
                Events["Open"].guiActiveEditor = true;
                Events["Close"].guiActiveEditor = true;
            }
			//recalculate volume and mass, start updating labels
			Setup();
			StartCoroutine(UpdateStatus());
			//if there are multiple hangars in the vessel,
			//coordinate with them before storing packed constructs
			build_checklist();
			//store packed constructs if any
			StartCoroutine(convert_constructs_to_vessels());
		}
		
		public void Setup(bool reset = false)	
		{
			//get launch speed if it's defined
			try { launchVelocity = LaunchVelocity != "" ? ConfigNode.ParseVector3(LaunchVelocity) : Vector3.zero; }
			catch(Exception ex)
			{
				Utils.Log("Unable to parse LaunchVelocity '{0}'", LaunchVelocity);
				Debug.LogException(ex);
			}
			//recalculate part and hangar metrics
			part_metric = new Metric(part);
			hangar_metric = HangarSpace != "" ? new Metric(part, HangarSpace) : null;
			//if hangar metric is not provided, derive it from part metric
			if(hangar_metric == null || hangar_metric.Empty)
				hangar_metric = part_metric*usefull_volume_ratio;
			//else if mesh should be used to calculate fits, get it as well
			else if(UseHangarSpaceMesh)
				hangar_space = part.FindModelComponent<MeshFilter>(HangarSpace);
			//setup vessels pack
			stored_vessels.space = hangar_metric;
			packed_constructs.space = hangar_metric;
			//display recalculated values
			hangar_v = Utils.formatVolume(hangar_metric.volume);
			hangar_d = Utils.formatDimensions(hangar_metric.size);
			//now recalculate used volume
			if(reset)
			{   //if resetting, try to repack vessels on resize
				List<PackedConstruct> constructs = packed_constructs.Values;
				packed_constructs.Clear();
				foreach(PackedConstruct pc in constructs)
					try_store_construct(pc);
				//no need to change_part_params as set_params is called later
			}
			//calculate used_volume
			used_volume = 0;
			foreach(StoredVessel sv in stored_vessels.Values) used_volume += sv.volume;
			foreach(PackedConstruct pc in packed_constructs.Values) used_volume += pc.volume;
			//then set other part parameters
			set_part_params(reset);
		}

		void set_part_params(bool reset = false) 
		{
			//reset values if needed
			if(base_mass < 0 || reset) base_mass = part.mass;
			if(vessels_mass < 0 || reset)
			{
				vessels_mass = 0;
				foreach(StoredVessel sv in stored_vessels.Values) vessels_mass += sv.mass;
				foreach(PackedConstruct pc in packed_constructs.Values) vessels_mass += pc.mass;
				stored_mass = Utils.formatMass(vessels_mass);
			}
			if(vessels_cost < 0 || reset)
			{
				vessels_cost = 0;
				foreach(StoredVessel sv in stored_vessels.Values) vessels_cost += sv.cost;
				foreach(PackedConstruct pc in packed_constructs.Values) vessels_cost += pc.cost;
				stored_cost = vessels_cost.ToString();
			}
			//set part mass
			part.mass = base_mass+vessels_mass;
			//calculate crew capacity from remaining volume
			if(!StaticCrewCapacity)
				part.CrewCapacity = (int)((part_metric.volume-hangar_metric.volume)*crew_volume_ratio/VolumePerKerbal);
			crew_capacity = part.CrewCapacity.ToString();
			//update Editor counters and all other that listen
			if(EditorLogic.fetch != null) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
		}

		void change_part_params(Metric delta, float k = 1f)
		{
			vessels_mass += k*delta.mass;
			vessels_cost += k*delta.cost;
			used_volume  += k*delta.volume;
			if(used_volume < 0) used_volume = 0;
			if(vessels_mass < 0) vessels_mass = 0;
			if(vessels_cost < 0) vessels_cost = 0;
			stored_mass = Utils.formatMass(vessels_mass);
			stored_cost = vessels_cost.ToString();
			set_part_params();
		}

		public float GetModuleCost() { return vessels_cost; }
		#endregion

		#region Save-Load
		public override void OnSave(ConfigNode node)
		{
			//hangar state
			node.AddValue("hangarState", hangar_state.ToString());
			//save stored vessels
			if(stored_vessels.Count > 0)
				stored_vessels.Save(node.AddNode("STORED_VESSELS"));
			if(packed_constructs.Count > 0)
				packed_constructs.Save(node.AddNode("PACKED_CONSTRUCTS"));
		}

		public override void OnLoad(ConfigNode node)
		{ 
			//hangar state
			if(node.HasValue("hangarState"))
				hangar_state = (HangarState)Enum.Parse(typeof(HangarState), node.GetValue("hangarState"));
			//restore stored vessels
			if(node.HasNode("STORED_VESSELS"))
				stored_vessels.Load(node.GetNode("STORED_VESSELS"));
			if(node.HasNode("PACKED_CONSTRUCTS"))
				packed_constructs.Load(node.GetNode("PACKED_CONSTRUCTS"));
		}
		#endregion

		#region Physics changes
		public void FixedUpdate()
		{
			if(HighLogic.LoadedSceneIsFlight)
			{
				//change vessel velocity if requested
				if(change_velocity)
				{
					vessel.ChangeWorldVelocity((Vector3d.zero+deltaV).xzy);
					change_velocity = false;
					deltaV = Vector3.zero;
				}
				//consume energy if hangar is operational
				if(hangar_state == HangarState.Active)
				{
					float request = EnergyConsumption*TimeWarp.fixedDeltaTime;
					if(part.RequestResource("ElectricCharge", request) < request)
					{
						ScreenMessager.showMessage("Not enough energy. The hangar has deactivated.");
						Deactivate();
					}
				}
			}
		}
		#endregion
		
		#region Store
		/// <summary>
		/// Checks if a vessel can be stored in the hangar right now.
		/// </summary>
		/// <param name="vsl">A vessel to check</param>
		bool can_store(Vessel vsl)
		{
			if(vsl == null || vsl == vessel || !vsl.enabled || vsl.isEVA) return false;
			//if hangar is not ready, return
			if(hangar_state == HangarState.Inactive) 
			{
				ScreenMessager.showMessage("Activate the hangar first");
				return false;
			}
			//check self state first
			switch(FlightGlobals.ClearToSave()) 
			{
			case ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH:
			{
				ScreenMessager.showMessage("Cannot accept the vessel while about to crush");
				return false;
			}
			}
			//always check relative velocity and acceleration
			Vector3 rv = vessel.GetObtVelocity()-vsl.GetObtVelocity();
			if(rv.magnitude > 1f) 
			{
				ScreenMessager.showMessage("Cannot accept a vessel with a relative speed higher than 1m/s");
				return false;
			}
			Vector3 ra = vessel.acceleration - vsl.acceleration;
			if(ra.magnitude > 0.1)
			{
				ScreenMessager.showMessage("Cannot accept an accelerating vessel");
				return false;
			}
			return true;
		}

		bool metric_fits_into_hangar_space(Metric m)
		{
			GetLaunchTransform();
			return hangar_space == null ? 
				m.FitsAligned(launch_transform, part.partTransform, hangar_metric) : 
				m.FitsAligned(launch_transform, hangar_space.transform, hangar_space.sharedMesh);
		}

		bool compute_hull { get { return hangar_space != null; } }

		StoredVessel try_store(Vessel vsl)
		{
			//check vessel crew
			if(vsl.GetCrewCount() > vessel.GetCrewCapacity()-vessel.GetCrewCount())
			{
				ScreenMessager.showMessage("Not enough space for the crew of a docking vessel");
				return null;
			}
			//check vessel metrics
			var sv = new StoredVessel(vsl, compute_hull);
			if(!metric_fits_into_hangar_space(sv.metric))
			{
				ScreenMessager.showMessage("Insufficient vessel clearance for safe docking\n" +
					"The vessel cannot be stored in this hangar");
				return null;
			}
			if(!stored_vessels.Add(sv))
			{
				ScreenMessager.showMessage("There's no room in the hangar for this vessel");
				return null;
			}
			return sv;
		}
		
		//store vessel
		void store_vessel(Vessel vsl, bool perform_checks = true)
		{
			StoredVessel stored_vessel;
			if(perform_checks) //for normal operation
			{
				//check if this vessel was encountered before;
				//if so, reset the timer and return
				MemoryTimer timer;
				if(probed_vessels.TryGetValue(vsl.id, out timer))
				{ timer.Reset(); return; }
				//if the vessel is new, check momentary states
				if(!can_store(vsl))	return;
				//if the state is OK, try to store the vessel
				stored_vessel = try_store(vsl);
				//if failed, remember it
				if(stored_vessel == null)
				{
					timer = new MemoryTimer();
					timer.EndAction += () => { if(probed_vessels.ContainsKey(vsl.id)) probed_vessels.Remove(vsl.id); };
					probed_vessels.Add(vsl.id, timer);
					StartCoroutine(timer);
					return;
				}
			}
			else //for storing packed constructs upon hangar launch
			{
				stored_vessel = new StoredVessel(vsl);
				stored_vessels.ForceAdd(stored_vessel);
			}
			//recalculate volume and mass
			change_part_params(stored_vessel.metric);
			//calculate velocity change to conserve impulse
			deltaV = (vsl.orbit.vel-vessel.orbit.vel)*stored_vessel.mass/vessel.GetTotalMass();
			change_velocity = true;
			//get vessel crew on board
			var _crew = new List<ProtoCrewMember>(stored_vessel.crew);
			CrewTransfer.delCrew(vsl, _crew);
			vsl.DespawnCrew();
			//first of, add crew to the hangar if there's a place
			CrewTransfer.addCrew(part, _crew);
			//then add to other vessel parts if needed
			CrewTransfer.addCrew(vessel, _crew);
			//switch to hangar vessel before storing
			if(FlightGlobals.ActiveVessel.id == vsl.id)
				FlightGlobals.ForceSetActiveVessel(vessel);
			//destroy vessel
			vsl.Die();
			ScreenMessager.showMessage("Vessel has been docked inside the hangar");
		}
		
		//called every frame while part collider is touching the trigger
		void OnTriggerStay (Collider col) //see Unity docs
		{
			if(hangar_state != HangarState.Active
				||  col == null
				|| !col.CompareTag ("Untagged")
				||  col.gameObject.name == "MapOverlay collider"// kethane
				||  col.attachedRigidbody == null)
				return;
			//get part and try to store vessel
			Part p = col.attachedRigidbody.GetComponent<Part>();
			if(p == null || p.vessel == null) return;
			store_vessel(p.vessel);
		}
		
		//called when part collider exits the trigger
		void OnTriggerExit(Collider col)
		{
			if(col == null
				|| !col.CompareTag("Untagged")
				||  col.gameObject.name == "MapOverlay collider" // kethane
				||  col.attachedRigidbody == null)
				return;
			Part p = col.attachedRigidbody.GetComponent<Part>();
			if(p == null || p.vessel == null) return;
		}
		#endregion

		#region EditHangarContents
		bool try_store_construct(PackedConstruct pc)
		{
			GetLaunchTransform();
			if(!metric_fits_into_hangar_space(pc.metric))
			{
				ScreenMessager.showMessage(5, "Insufficient vessel clearance for safe docking\n" +
					"\"{0}\" cannot be stored in this hangar", pc.name);
				return false;
			}
			if(!packed_constructs.Add(pc))
			{
				ScreenMessager.showMessage("There's no room in the hangar for \"{0}\"", pc.name);
				return false;
			}
			return true;
		}

		IEnumerator<YieldInstruction> delayed_try_store_construct(PackedConstruct pc)
		{
			if(pc.construct == null) yield break;
			Utils.LockEditor(scLock);
			for(int i = 0; i < 3; i++)
				yield return new WaitForEndOfFrame();
			pc.UpdateMetric(compute_hull);
			if(try_store_construct(pc)) 
				change_part_params(pc.metric);
			pc.UnloadConstruct();
			Utils.LockEditor(scLock, false);
		}

		void vessel_selected(string filename, string flagname)
		{
			EditorLogic EL = EditorLogic.fetch;
			if(EL == null) return;
			//load vessel config
			vessel_selector = null;
			var pc = new PackedConstruct(filename, flagname);
			if(pc.construct == null) 
			{
				Utils.Log("PackedConstruct: unable to load ShipConstruct from {0}. " +
					"This usually means that some parts are missing " +
					"or some modules failed to initialize.", filename);
				ScreenMessager.showMessage("Unable to load {0}", filename);
				return;
			}
			//check if the construct contains launch clamps
			if(Utils.HasLaunchClamp(pc.construct))
			{
				ScreenMessager.showMessage("\"{0}\" has launch clamps. Remove them before storing.", pc.name);
				pc.UnloadConstruct();
				return;
			}
			//check if it's possible to launch such vessel
			bool cant_launch = false;
			var preFlightCheck = new PreFlightCheck(new Callback(() => cant_launch = false), new Callback(() => cant_launch = true));
			preFlightCheck.AddTest(new PreFlightTests.ExperimentalPartsAvailable(pc.construct));
			preFlightCheck.RunTests(); 
			//cleanup loaded parts and try to store construct
			if(cant_launch) pc.UnloadConstruct();
			else StartCoroutine(delayed_try_store_construct(pc));
		}
		void selection_canceled() { vessel_selector = null; }

		void remove_construct(PackedConstruct pc)
		{
			change_part_params(pc.metric, -1f);
			packed_constructs.Remove(pc);
		}

		void clear_constructs() 
		{ foreach(PackedConstruct pc in packed_constructs.Values) remove_construct(pc); }

		IEnumerator<YieldInstruction> convert_constructs_to_vessels()
		{
			if(HighLogic.LoadedScene != GameScenes.FLIGHT ||
				packed_constructs.Count == 0) 
			{ Ready = true;	yield break; }
			//temporarely deactivate the hangar
			HangarState cur_state = hangar_state; Deactivate();
			//wait for hangar.vessel to be loaded
			var self = new VesselWaiter(vessel);
			while(!self.loaded) yield return null;
			while(!enabled) yield return null;
			//wait for other hangars to be ready
			while(!other_hangars_ready) yield return null;
			//create vessels from constructs and store them
			foreach(PackedConstruct pc in packed_constructs.Values)
			{
				remove_construct(pc);
				if(!pc.LoadConstruct()) 
				{
					Utils.Log("PackedConstruct: unable to load ShipConstruct {0}. " +
						"This usually means that some parts are missing " +
						"or some modules failed to initialize.", pc.name);
					ScreenMessager.showMessage("Unable to load {0}", pc.name);
					continue;
				}
				ShipConstruction.PutShipToGround(pc.construct, get_transform_for_construct(pc));
				ShipConstruction.AssembleForLaunch(pc.construct, 
					vessel.vesselName+":"+HangarName, pc.flag, 
					FlightDriver.FlightStateCache,
					new VesselCrewManifest());
				var vsl = new VesselWaiter(FlightGlobals.Vessels[FlightGlobals.Vessels.Count - 1]);
				FlightGlobals.ForceSetActiveVessel(vsl.vessel);
				Staging.beginFlight();
				//wait for vsl to be launched
				while(!vsl.loaded) yield return null;
				store_vessel(vsl.vessel, false);
				//wait a 0.1 sec, otherwise the vessel may not be destroyed properly
				yield return new WaitForSeconds(0.1f); 

			}
			stored_mass = Utils.formatMass(vessels_mass);
			if(cur_state == HangarState.Active) Activate();
			//save game afterwards
			FlightGlobals.ForceSetActiveVessel(vessel);
			while(!self.loaded) yield return null;
			yield return new WaitForSeconds(0.5f);
			GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
			Ready = true;
		}
		#endregion

		#region Restore
		#region Positioning
		/// <summary>
		/// Calculate transform of restored vessel.
		/// </summary>
		public Transform GetLaunchTransform()
		{
			launch_transform = null;
			if(LaunchTransform != "")
				launch_transform = part.FindModelTransform(LaunchTransform);
			if(launch_transform == null)
			{
				Vector3 offset = Vector3.up * LaunchHeightOffset;
				Transform t = part.transform;
				var restorePos = new GameObject ();
				restorePos.transform.position = t.position;
				restorePos.transform.position += t.TransformDirection (offset);
				restorePos.transform.rotation = t.rotation;
				launch_transform = restorePos.transform;
				Utils.Log("LaunchTransform not found. Using offset.");
			}
			return launch_transform;
		}

		Transform get_transform_for_construct(PackedConstruct pc)
		{
			GetLaunchTransform();
			var tmp = new GameObject();
			Vector3 bounds_offset  = launch_transform.TransformDirection(pc.metric.center);
			tmp.transform.position = launch_transform.position+bounds_offset;
			tmp.transform.rotation = launch_transform.rotation;
			return tmp.transform;
		}
		
		/// <summary>
		/// Set vessel orbit, transform, coordinates.
		/// </summary>
		/// <param name="sv">Stored vessel</param>
		void position_vessel(StoredVessel sv)
		{
			ProtoVessel pv = sv.vessel;
			//state
			pv.splashed = vessel.Landed;
			pv.landed   = vessel.Splashed;
			//rotation
			//it is essential to use BackupVessel() instead of vessel.protoVessel, 
			//because in general the latter does not store the current flight state of the vessel
			ProtoVessel hpv = vessel.BackupVessel();
			Quaternion proto_rot  = hpv.rotation;
			Quaternion hangar_rot = vessel.vesselTransform.rotation;
			//rotate launchTransform.rotation to protovessel's reference frame
			pv.rotation = proto_rot*hangar_rot.Inverse()*launch_transform.rotation;
			//calculate launch offset from vessel bounds
			Vector3 bounds_offset = launch_transform.TransformDirection(sv.CoM - sv.CoG);
			//set vessel's orbit
			double UT  = Planetarium.GetUniversalTime();
			Orbit horb = vessel.orbit;
			var vorb = new Orbit();
			Vector3 d_pos = launch_transform.position-vessel.findWorldCenterOfMass()+bounds_offset;
			Vector3d vpos = horb.pos+new Vector3d(d_pos.x, d_pos.z, d_pos.y);
			Vector3d vvel = horb.vel;
			if(LaunchWithPunch && launchVelocity != Vector3.zero)
			{
				//honor the impulse conservation law
				//:calculate launched vessel velocity
				float tM = vessel.GetTotalMass();
				float hM = tM - sv.mass;
				Vector3 d_vel = launch_transform.TransformDirection(launchVelocity);
				vvel += (Vector3d.zero + d_vel*hM/tM).xzy;
				//:calculate hangar's vessel velocity
				Vector3d hvel = horb.vel + (Vector3d.zero + d_vel*(-sv.mass)/tM).xzy;
				vessel.orbitDriver.SetOrbitMode(OrbitDriver.UpdateMode.UPDATE);
				horb.UpdateFromStateVectors(horb.pos, hvel, horb.referenceBody, UT);
			}
			vorb.UpdateFromStateVectors(vpos, vvel, horb.referenceBody, UT);
			pv.orbitSnapShot = new OrbitSnapshot(vorb);
			//position on a surface
			if(vessel.LandedOrSplashed)
			{
				//calculate launch offset from vessel bounds
				bounds_offset = launch_transform.TransformDirection(-sv.CoG);
				//set vessel's position
				vpos = Vector3d.zero+launch_transform.position+bounds_offset;
				pv.longitude  = vessel.mainBody.GetLongitude(vpos);
				pv.latitude   = vessel.mainBody.GetLatitude(vpos);
				pv.altitude   = vessel.mainBody.GetAltitude(vpos);
			}
		}

		//static coroutine launched from a DontDestroyOnLoad sentinel object allows to execute code while the scene is switching
		static IEnumerator<YieldInstruction> setup_vessel(UnityEngine.Object sentinel, LaunchedVessel lv)
		{
			while(!lv.loaded) yield return null;
			lv.tunePosition();
			lv.transferCrew();
			//it seems you must give KSP a moment to sort it all out,
            //so delay the remaining steps of the transfer process. 
			//(from the CrewManifest: https://github.com/sarbian/CrewManifest/blob/master/CrewManifest/ManifestController.cs)
			yield return new WaitForSeconds(0.25f);
			lv.vessel.SpawnCrew();
			Destroy(sentinel);
		}
		
		public static void SetupVessel(LaunchedVessel lv)
		{
			var obj = new GameObject();
			DontDestroyOnLoad(obj);
			obj.AddComponent<MonoBehaviour>().StartCoroutine(setup_vessel(obj, lv));
		}
		#endregion
		
		#region Resources
		public void prepareResourceList(StoredVessel sv)
		{
			if(resourceTransferList.Count > 0) return;
			foreach(var r in sv.resources.resourcesNames)
			{
				if(hangarResources.ResourceCapacity(r) == 0) continue;
				var rm = new ResourceManifest();
				rm.name          = r;
				rm.amount        = sv.resources.ResourceAmount(r);
				rm.capacity      = sv.resources.ResourceCapacity(r);
				rm.offset        = rm.amount;
				rm.host_amount   = hangarResources.ResourceAmount(r);
				rm.host_capacity = hangarResources.ResourceCapacity(r);
				rm.pool          = rm.host_amount + rm.offset;
				rm.minAmount     = Math.Max(0, rm.pool-rm.host_capacity);
				rm.maxAmount     = Math.Min(rm.pool, rm.capacity);
				resourceTransferList.Add(rm);
			}
		}
		
		public void updateResourceList()
		{
			update_resources();
			foreach(ResourceManifest rm in resourceTransferList)
			{
				rm.host_amount = hangarResources.ResourceAmount(rm.name);
				rm.pool        = rm.host_amount + rm.offset;
				rm.minAmount   = Math.Max(0, rm.pool-rm.host_capacity);
				rm.maxAmount   = Math.Min(rm.pool, rm.capacity);
			}
		}
		
		public void transferResources(StoredVessel sv)
		{
			if(resourceTransferList.Count == 0) return;
			foreach(var r in resourceTransferList)
			{
				//transfer resource between hangar and protovessel
				var a = hangarResources.TransferResource(r.name, r.offset-r.amount);
				a = r.amount-r.offset + a;
				var b = sv.resources.TransferResource(r.name, a);
				hangarResources.TransferResource(r.name, b);
				//update masses
				PartResourceDefinition res_def = PartResourceLibrary.Instance.GetDefinition(r.name);
				if(res_def.density == 0) continue;
				float dM = (float)a*res_def.density;
				float dC = (float)a*res_def.unitCost;
				vessels_mass += dM;
				vessels_cost += dC;
				sv.mass += dM;
				sv.cost += dC;
				set_part_params();
			}
			resourceTransferList.Clear();
		}
		#endregion
		
		bool can_restore()
		{
			//if hangar is not ready, return
			if(hangar_state == HangarState.Inactive) 
			{
				ScreenMessager.showMessage("Activate the hangar first");
				return false;
			}
			if(hangar_gates.State != AnimatorState.Opened) 
			{
				ScreenMessager.showMessage("Open hangar gates first");
				return false;
			}
			//if something is docked to the hangar docking port (if its present)
			ModuleDockingNode dport = part.GetModule<ModuleDockingNode>();
			if(dport != null && dport.vesselInfo != null)
			{
				ScreenMessager.showMessage("Cannot launch a vessel while another is docked");
				return false;
			}
			//if in orbit or on the ground and not moving
			switch(FlightGlobals.ClearToSave()) 
			{
				case ClearToSaveStatus.NOT_IN_ATMOSPHERE:
				{
					ScreenMessager.showMessage("Cannot launch a vessel while flying in atmosphere");
					return false;
				}
				case ClearToSaveStatus.NOT_UNDER_ACCELERATION:
				{
					ScreenMessager.showMessage("Cannot launch a vessel hangar is under accelleration");
					return false;
				}
				case ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH:
				{
					ScreenMessager.showMessage("Cannot launch a vessel while about to crush");
					return false;
				}
				case ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE:
				{
					ScreenMessager.showMessage("Cannot launch a vessel while moving over the surface");
					return false;
				}
			}
			if(vessel.angularVelocity.magnitude > 0.003)
			{
				ScreenMessager.showMessage("Cannot launch a vessel while rotating");
				return false;
			}
			return true;
		}
		
		public void TryRestoreVessel(StoredVessel stored_vessel)
		{
			if(!can_restore()) return;
			//clean up
			if(!stored_vessels.Remove(stored_vessel))
			{
				ScreenMessager.showMessage("WARNING: restored vessel ID is not found in the Stored Vessels: {0}\n" +
					"This should never happen!", stored_vessel.id);
				return;
			}
			ScreenMessager.showMessage("Launching \"{0}\"...", stored_vessel.name);
			//switch hangar state
			hangar_state = HangarState.Inactive;
			//transfer resources
			transferResources(stored_vessel);
			//set restored vessel orbit
			GetLaunchTransform();
			position_vessel(stored_vessel);
			//restore vessel
			stored_vessel.Load();
			//get restored vessel from the world
			launched_vessel = stored_vessel.launched_vessel;
			//transfer crew back to the launched vessel
			List<ProtoCrewMember> crew_to_transfer = CrewTransfer.delCrew(vessel, stored_vessel.crew);
			//change volume and mass
			change_part_params(stored_vessel.metric, -1f);
			//switch to restored vessel
			//:set launched vessel's state to flight
			// otherwise launched rovers are sometimes stuck to the ground despite of the launch_transform
			launched_vessel.Splashed = launched_vessel.Landed = false; 
			FlightGlobals.ForceSetActiveVessel(launched_vessel);
			SetupVessel(new LaunchedVessel(stored_vessel, launched_vessel, crew_to_transfer));
		}
		#endregion

		#region Events&Actions
		//events
		[KSPEvent (guiActiveEditor = true, guiName = "Open gates", active = true)]
		public void Open() 
		{ 
			hangar_gates.Open();
			Events["Open"].active = false;
			Events["Close"].active = true;
		}
	
		[KSPEvent (guiActiveEditor = true, guiName = "Close gates", active = false)]
		public void Close()	
		{ 
			hangar_gates.Close(); 
			Events["Open"].active = true;
			Events["Close"].active = false;
		}

		[KSPEvent (guiActive = true, guiActiveEditor = true, guiName = "Rename Hangar", active = true)]
		public void EditName() 
		{ 
			editing_hangar_name = !editing_hangar_name; 
			Utils.LockIfMouseOver(eLock, neWindowPos, editing_hangar_name);
		}
		bool editing_hangar_name = false;
		
		public void Activate() { hangar_state = HangarState.Active;	}
		
		public void Deactivate() 
		{ 
			hangar_state = HangarState.Inactive;
			foreach(MemoryTimer timer in probed_vessels.Values)
				StopCoroutine(timer);
			probed_vessels.Clear(); 
		}
		
		public void Toggle()
		{
			if(hangar_state == HangarState.Active) Deactivate();
			else Activate();
		}

		[KSPEvent (guiActiveEditor = true, guiName = "Edit contents", active = true)]
		public void EditHangar() 
		{ 
			editing_hangar = !editing_hangar; 
			Utils.LockIfMouseOver(eLock, eWindowPos, editing_hangar);
		}
		bool editing_hangar = false;

		//actions
		[KSPAction("Open gates")]
        public void OpenGatesAction(KSPActionParam param) { Open(); }
		
		[KSPAction("Close gates")]
        public void CloseGatesAction(KSPActionParam param) { Close(); }
		
		[KSPAction("Toggle gates")]
        public void ToggleGatesAction(KSPActionParam param) { hangar_gates.Toggle(); }
		
		[KSPAction("Activate hangar")]
        public void ActivateStateAction(KSPActionParam param) { Activate(); }
		
		[KSPAction("Deactivate hangar")]
        public void DeactivateStateAction(KSPActionParam param) { Deactivate(); }
		
		[KSPAction("Toggle hangar")]
        public void ToggleStateAction(KSPActionParam param) { Toggle(); }
		#endregion
	
		#region OnGUI
		void hangar_content_editor(int windowID)
		{
			GUILayout.BeginVertical();
			GUILayout.BeginHorizontal();
			//VAB / SPH / SubAss selection
			GUILayout.FlexibleSpace();
			for(var T = VesselType.VAB; T <= VesselType.SubAssembly; T++)
			{ if(GUILayout.Toggle(vessel_type == T, T.ToString(), GUILayout.Width(100))) vessel_type = T; }
			GUILayout.FlexibleSpace();
			//Vessel selector
			if(GUILayout.Button("Select Vessel", Styles.normal_button, GUILayout.ExpandWidth(true))) 
			{
				var sWindowPos  = new Rect(eWindowPos) { height = 500 };
				var  diff  = HighLogic.CurrentGame.Parameters.Difficulty;
				bool stock = diff.AllowStockVessels;
				if(vessel_type == VesselType.SubAssembly) diff.AllowStockVessels = false;
				vessel_selector = 
					new CraftBrowser(sWindowPos, 
									 vessel_dirs[(int)vessel_type],
									 HighLogic.SaveFolder, "Select a ship to store",
					                 vessel_selected,
					                 selection_canceled,
					                 HighLogic.Skin,
					                 EditorLogic.ShipFileImage, true);
				diff.AllowStockVessels = stock;
			}
			GUILayout.EndHorizontal();
			//hangar info
			float used_frac = used_volume/hangar_metric.volume;
			GUILayout.Label(string.Format("Used Volume: {0}   {1:F1}%", 
			                              Utils.formatVolume(used_volume), used_frac*100f), 
			                Styles.fracStyle(1-used_frac), GUILayout.ExpandWidth(true));
			//hangar contents
			List<PackedConstruct> constructs = packed_constructs.Values;
			constructs.Sort((a, b) => a.name.CompareTo(b.name));
			scroll_view = GUILayout.BeginScrollView(scroll_view, GUILayout.Height(200), GUILayout.Width(400));
			GUILayout.BeginVertical();
			foreach(PackedConstruct pc in constructs)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(string.Format("{0}: {1}   Cost: {2:F1}", 
				                              pc.name, Utils.formatMass(pc.metric.mass), pc.metric.cost), 
				                Styles.label, GUILayout.ExpandWidth(true));
				if(GUILayout.Button("+1", Styles.green_button, GUILayout.Width(25))) 
				{ if(try_store_construct(pc.Clone())) change_part_params(pc.metric); }
				if(GUILayout.Button("X", Styles.red_button, GUILayout.Width(25))) remove_construct(pc);
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
			GUILayout.EndScrollView();
			if(GUILayout.Button("Clear", Styles.red_button, GUILayout.ExpandWidth(true))) clear_constructs();
			if(GUILayout.Button("Close", Styles.normal_button, GUILayout.ExpandWidth(true))) 
			{
				Utils.LockEditor(eLock, false);
				editing_hangar = false;
			}
			GUILayout.EndVertical();
			GUI.DragWindow(new Rect(0, 0, 500, 20));
		}

		void hangar_name_editor(int windowID)
		{
			GUILayout.BeginVertical();
			HangarName = GUILayout.TextField(HangarName, 50);
			if(GUILayout.Button("Close", Styles.normal_button, GUILayout.ExpandWidth(true))) 
			{
				Utils.LockEditor(eLock, false);
				editing_hangar_name = false;
			}
			GUILayout.EndVertical();
			GUI.DragWindow(new Rect(0, 0, 500, 20));
		}

		public void OnGUI() 
		{ 
			if(Event.current.type != EventType.Layout) return;
			if(!editing_hangar && !editing_hangar_name) return;
			if(editing_hangar && 
				(HighLogic.LoadedScene != GameScenes.EDITOR &&
				 HighLogic.LoadedScene != GameScenes.SPH)) return;
			//init skin
			Styles.InitSkin();
			GUI.skin = Styles.skin;
			Styles.InitGUI();
			//edit hangar
			if(editing_hangar)
			{
				if(vessel_selector == null) 
				{
					Utils.LockIfMouseOver(eLock, eWindowPos);
					eWindowPos = GUILayout.Window(GetInstanceID(), eWindowPos,
								                  hangar_content_editor,
								                  "Choose vessel type",
								                  GUILayout.Width(400));
					AddonWindowBase<HangarWindow>.CheckRect(ref eWindowPos);
				}
				else 
				{
					Utils.LockIfMouseOver(eLock, vessel_selector.windowRect);
					vessel_selector.OnGUI();
				}
			}
			//edit name
			else if(editing_hangar_name)
			{
				Utils.LockIfMouseOver(eLock, neWindowPos);
				neWindowPos = GUILayout.Window(GetInstanceID(), neWindowPos,
											   hangar_name_editor,
											   "Rename Hangar",
											   GUILayout.Width(400));
				AddonWindowBase<HangarWindow>.CheckRect(ref neWindowPos);
			}
		}
		#endregion

		#region ControllableModule
		ModuleGUIState gui_state;
		public bool CanEnable() { return true; }
		public bool CanDisable() 
		{ 
			if(stored_vessels.Count > 0 || packed_constructs.Count > 0)
			{
				ScreenMessager.showMessage("Empty the hangar before deflating it");
				return false;
			}
			if(EditorLogic.fetch == null && hangar_state == HangarState.Active)
			{
				ScreenMessager.showMessage("Deactivate the hangar before deflating it");
				return false;
			}
			if(hangar_gates.State != AnimatorState.Closed)
			{
				ScreenMessager.showMessage("Close hangar doors before deflating it");
				return false;
			}
			return true;
		}

		public void Enable(bool enable) 
		{ 
			if(enable) 
			{
				if(gui_state == null) gui_state = this.SaveGUIState();
				this.ActivateGUI(gui_state);
				Setup();
				enabled = true;
			}
			else
			{
				gui_state = this.DeactivateGUI();
				enabled = false;
			}
		}
		#endregion
	}
}