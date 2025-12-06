using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.Station.Components;
using Content.Server.Cargo.Systems;
using Robust.Shared.Timing; // For IGameTiming
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Content.Shared._NF.CCVar;
using Robust.Shared.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Content.Server._NF.Station.Components;
using Content.Server.Storage.Components;
using Content.Shared._Mono.Shipyard;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Content.Shared.Shuttles.Components; // For IFFComponent
using Content.Shared.Timing;
using Content.Server.Gravity;
using Robust.Shared.Physics;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components; // For GravitySystem
using Content.Shared.Doors.Components;
using Robust.Shared.Map.Components;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShipOwnershipSystem _shipOwnership = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!; // For safe container removal before deletion
    [Dependency] private readonly UseDelaySystem _useDelay = default!;
    [Dependency] private readonly GravitySystem _gravitySystem = default!; // For post-load gravity refresh
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!; // For cooldown timing

    private EntityQuery<TransformComponent> _transformQuery;

    public MapId? ShipyardMap { get; private set; }
    private float _shuttleIndex;
    private const float ShuttleSpawnBuffer = 1f;
    private ISawmill _sawmill = default!;
    private bool _enabled;
    private float _baseSaleRate;
    private readonly Dictionary<EntityUid, TimeSpan> _lastLoadCharge = new(); // Per-player load charge cooldown

    // The type of error from the attempted sale of a ship.
    public enum ShipyardSaleError
    {
        Success, // Ship can be sold.
        Undocked, // Ship is not docked with the station.
        OrganicsAboard, // Sapient intelligence is aboard, cannot sell, would delete the organics
        InvalidShip, // Ship is invalid
        MessageOverwritten, // Overwritten message.
    }

    // TODO: swap to strictly being a formatted message.
    public struct ShipyardSaleResult
    {
        public ShipyardSaleError Error; // Whether or not the ship can be sold.
        public string? OrganicName; // In case an organic is aboard, this will be set to the first that's aboard.
        public string? OverwrittenMessage; // The message to write if Error is MessageOverwritten.
    }

    public override void Initialize()
    {
        base.Initialize();

        _transformQuery = GetEntityQuery<TransformComponent>();

        // FIXME: Load-bearing jank - game doesn't want to create a shipyard map at this point.
        _enabled = _configManager.GetCVar(NFCCVars.Shipyard);
        _configManager.OnValueChanged(NFCCVars.Shipyard, SetShipyardEnabled); // NOTE: run immediately set to false, see comment above

        _configManager.OnValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate, true);
    _sawmill = Logger.GetSawmill("shipyard");
    SubscribeNetworkEvent<RequestLoadShipMessage>(HandleLoadShipRequest);

        SubscribeLocalEvent<ShipyardConsoleComponent, ComponentStartup>(OnShipyardStartup);
        SubscribeLocalEvent<ShipyardConsoleComponent, BoundUIOpenedEvent>(OnConsoleUIOpened);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSellMessage>(OnSellMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsolePurchaseMessage>(OnPurchaseMessage);
        // Ship saving/loading functionality
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleLoadMessage>(OnLoadMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntInsertedIntoContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntRemovedFromContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<StationDeedSpawnerComponent, MapInitEvent>(OnInitDeedSpawner);
    }

    public override void Shutdown()
    {
        _configManager.UnsubValueChanged(NFCCVars.Shipyard, SetShipyardEnabled);
        _configManager.UnsubValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate);
    }
    private void OnShipyardStartup(EntityUid uid, ShipyardConsoleComponent component, ComponentStartup args)
    {
        if (!_enabled)
            return;
        InitializeConsole();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        CleanupShipyard();
    }

    private void SetShipyardEnabled(bool value)
    {
        if (_enabled == value)
            return;

        _enabled = value;

        if (value)
            SetupShipyardIfNeeded();
        else
            CleanupShipyard();
    }

    private void SetShipyardSellRate(float value)
    {
        _baseSaleRate = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Adds a ship to the shipyard, calculates its price, and attempts to ftl-dock it to the given station
    /// </summary>
    /// <param name="stationUid">The ID of the station to dock the shuttle to</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was purchased</param>
    public bool TryPurchaseShuttle(EntityUid stationUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || !TryAddShuttle(shuttlePath, out var shuttleGrid)
            || !TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        if (!TryAddShuttle(shuttlePath, out var shuttleGrid))
        {
            shuttleEntityUid = null;
            return false;
        }

        var grid = shuttleGrid.Value;

        if (!TryComp<ShuttleComponent>(grid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        var price = _pricing.AppraiseGrid(grid, null);
        var targetGrid = consoleXform.GridUid.Value;

        _sawmill.Info($"Shuttle {shuttlePath} was purchased at {ToPrettyString(consoleUid)} for {price:f2}");

        // Ensure required components for docking and identification
        EntityManager.EnsureComponent<Robust.Shared.Physics.Components.PhysicsComponent>(grid);
        EntityManager.EnsureComponent<ShuttleComponent>(grid);
        var iff = EntityManager.EnsureComponent<IFFComponent>(grid);
        // Add new grid to the same station as the console's grid (for IFF / ownership), if any
        if (TryComp<StationMemberComponent>(consoleXform.GridUid, out var stationMember))
        {
            _station.AddGridToStation(stationMember.Station, grid);
        }

        _shuttle.TryFTLDock(grid, shuttleComponent, targetGrid);
        shuttleEntityUid = grid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle from a file and docks it to the grid the console is on, like ship purchases.
    /// This is used for loading saved ships.
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    public bool TryPurchaseShuttleFromFile(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        // Get the grid the console is on
        if (!_transformQuery.TryComp(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
        {
            shuttleEntityUid = null;
            return false;
        }

        if (!TryAddShuttle(shuttlePath, out var shuttleGrid))
        {
            shuttleEntityUid = null;
            return false;
        }

        var grid = shuttleGrid.Value;

        if (!TryComp<ShuttleComponent>(grid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        _sawmill.Info($"Shuttle {shuttlePath} was purchased at {ToPrettyString(stationUid)} for {price:f2}");
        var ev = new ShipBoughtEvent();
        RaiseLocalEvent(shuttleGrid.Value, ev);
        //can do TryFTLDock later instead if we need to keep the shipyard map paused

        // Ensure required components for docking and identification
        EnsureComp<PhysicsComponent>(grid);
        EnsureComp<ShuttleComponent>(grid);
        EnsureComp<IFFComponent>(grid);

        // Load-time sanitation: purge any deserialized joints and reset dock joint references
        // to avoid physics processing invalid joint bodies (e.g., Entity 0) from YAML.
        try
        {
            PurgeJointsAndResetDocks(grid);
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"[ShipLoad] PurgeJointsAndResetDocks failed on {grid}: {ex.Message}");
        }
        // Add new grid to the same station as the console's grid (for IFF / ownership), if any
        if (TryComp<StationMemberComponent>(consoleXform.GridUid, out var stationMember))
        {
            _station.AddGridToStation(stationMember.Station, grid);
        }

        _shuttle.TryFTLDock(grid, shuttleComponent, targetGrid.Value);
        shuttleEntityUid = grid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle into the ShipyardMap from a file path
    /// </summary>
    /// <param name="shuttlePath">The path to the grid file to load. Must be a grid file!</param>
    /// <returns>Returns the EntityUid of the shuttle</returns>
    private bool TryAddShuttle(ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleGrid)
    {
        shuttleGrid = null;
        SetupShipyardIfNeeded();
        if (ShipyardMap == null)
            return false;

        if (!_mapLoader.TryLoadGrid(ShipyardMap.Value, shuttlePath, out var grid, offset: new Vector2(500f + _shuttleIndex, 1f)))
        {
            _sawmill.Error($"Unable to spawn shuttle {shuttlePath}");
            return false;
        }

        _shuttleIndex += grid.Value.Comp.LocalAABB.Width + ShuttleSpawnBuffer;

        shuttleGrid = grid.Value.Owner;
        return true;
    }

    /// <summary>
    /// Writes YAML data to a temporary file and loads it using the exact same method as purchasing a shuttle from a file.
    /// Ensures identical setup/docking logic.
    /// </summary>
    private bool TryPurchaseShuttleFromYamlData(EntityUid consoleUid, string yamlData, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;
        ResPath tempPath = default;
        try
        {
            // Create a temp path under UserData/ShipyardTemp
            var fileName = $"shipyard_load_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.yml";
            var dir = new ResPath("/") / "UserData" / "ShipyardTemp";
            tempPath = dir / fileName;

            // Ensure directory exists and write file
            _resources.UserData.CreateDir(dir);
            using (var writer = _resources.UserData.OpenWriteText(tempPath))
            {
                writer.Write(yamlData);
            }

            // Reuse purchase-from-file flow
            if (!TryPurchaseShuttleFromFile(consoleUid, tempPath, out shuttleEntityUid))
                return false;

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to purchase shuttle from YAML data: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (tempPath != default && _resources.UserData.Exists(tempPath))
                    _resources.UserData.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Tries to reset the delays on any entities with the UseDelayComponent.
    /// Needed to ensure items don't have prolonged delays after saving.
    /// </summary>
    private void TryResetUseDelays(EntityUid shuttleGrid)
    {
        var useDelayQuery = _entityManager.EntityQueryEnumerator<UseDelayComponent, TransformComponent>();

        while (useDelayQuery.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.GridUid != shuttleGrid)
                continue;

            _useDelay.ResetAllDelays((uid, comp));
        }
    }

    /// <summary>
    /// Safely deletes an entity by ensuring it is first removed from any container relationships, and
    /// recursively clears any contents if the entity itself owns containers. This avoids client-side
    /// asserts when an entity is detached to null-space while still flagged as InContainer.
    /// </summary>
    private void SafeDelete(EntityUid uid)
    {
        try
        {
            // If this entity owns containers, empty them first.
            if (TryComp<ContainerManagerComponent>(uid, out var manager))
            {
                foreach (var container in manager.Containers.Values)
                {
                    // Copy to avoid modifying during iteration
                    foreach (var contained in container.ContainedEntities.ToArray())
                    {
                        try
                        {
                            _container.Remove(contained, container, force: true);
                        }
                        catch { /* best-effort */ }

                        // Recursively ensure any nested containers are emptied then delete.
                        SafeDelete(contained);
                    }
                }
            }

            // Ensure the entity itself is not inside a container anymore (paranoia in case callers misclassify parent).
            _container.TryRemoveFromContainer(uid);
        }
        catch { /* best-effort */ }

        // Finally queue the deletion of the entity itself.
        QueueDel(uid);
    }

    /// <summary>
    /// Removes any JointComponent instances that may have been deserialized with the ship and clears
    /// DockingComponent joint references. This prevents the physics solver from encountering joints
    /// with invalid body UIDs (e.g., default/zero) originating from stale YAML state. The DockingSystem
    /// will recreate proper weld joints during docking.
    /// </summary>
    private void PurgeJointsAndResetDocks(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var gridComponent))
            return;

        // Remove any JointComponent on the grid or its children
        var removed = 0;
        foreach (var uid in _lookup.GetEntitiesIntersecting(gridUid, gridComponent.LocalAABB))
        {
            // Purge joints first
            if (RemComp<JointComponent>(uid))
                removed++;

            // Reset docking joint references to force clean joint creation later
            if (TryComp<DockingComponent>(uid, out var dock))
            {
                dock.DockJoint = null;
                dock.DockJointId = null;

                // Clear malformed DockedWith values that might have come from YAML
                if (dock.DockedWith != null)
                {
                    var other = dock.DockedWith.Value;
                    if (!other.IsValid() || !HasComp<MetaDataComponent>(other))
                        dock.DockedWith = null;
                }
            }
        }

        if (removed > 0)
            _sawmill.Info($"[ShipLoad] Purged {removed} deserialized JointComponent(s) on grid {gridUid}");
    }

    /// <summary>
    /// Checks a shuttle to make sure that it is docked to the given station, and that there are no lifeforms aboard. Then it teleports tagged items on top of the console, appraises the grid, outputs to the server log, and deletes the grid
    /// </summary>
    /// <param name="stationUid">The ID of the station that the shuttle is docked to</param>
    /// <param name="shuttleUid">The grid ID of the shuttle to be appraised and sold</param>
    /// <param name="consoleUid">The ID of the console being used to sell the ship</param>
    public ShipyardSaleResult TrySellShuttle(EntityUid stationUid, EntityUid shuttleUid, EntityUid consoleUid, out int bill)
    {
        ShipyardSaleResult result = new ShipyardSaleResult();
        bill = 0;

        if (!TryComp<StationDataComponent>(stationUid, out var stationGrid)
            || !HasComp<ShuttleComponent>(shuttleUid)
            || !TryComp(shuttleUid, out TransformComponent? xform)
            || ShipyardMap == null)
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var targetGrid = _station.GetLargestGrid(stationGrid);

        if (targetGrid == null)
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var gridDocks = _docking.GetDocks(targetGrid.Value);
        var shuttleDocks = _docking.GetDocks(shuttleUid);
        var isDocked = false;

        foreach (var shuttleDock in shuttleDocks)
        {
            foreach (var gridDock in gridDocks)
            {
                if (shuttleDock.Comp.DockedWith == gridDock.Owner)
                {
                    isDocked = true;
                    break;
                }
            }
            if (isDocked)
                break;
        }

        if (!isDocked)
        {
            _sawmill.Warning($"shuttle is not docked to that station");
            result.Error = ShipyardSaleError.Undocked;
            return result;
        }

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        var charName = FoundOrganics(shuttleUid, mobQuery, xformQuery);
        if (charName is not null)
        {
            _sawmill.Warning($"organics on board");
            result.Error = ShipyardSaleError.OrganicsAboard;
            result.OrganicName = charName;
            return result;
        }

        //just yeet and delete for now. Might want to split it into another function later to send back to the shipyard map first to pause for something
        //also superman 3 moment
        if (_station.GetOwningStation(shuttleUid) is { Valid: true } shuttleStationUid)
        {
            _station.DeleteStation(shuttleStationUid);
        }

        if (TryComp<ShipyardConsoleComponent>(consoleUid, out var comp))
        {
            CleanGrid(shuttleUid, consoleUid);
        }

        bill = (int)_pricing.AppraiseGrid(shuttleUid, LacksPreserveOnSaleComp);
        QueueDel(shuttleUid);
        _sawmill.Info($"Sold shuttle {shuttleUid} for {bill}");

        // Update all record UI (skip records, no new records)
        _shuttleRecordsSystem.RefreshStateForAll(true);

        result.Error = ShipyardSaleError.Success;
        return result;
    }

    private void CleanGrid(EntityUid grid, EntityUid destination)
    {
        var xform = Transform(grid);
        var enumerator = xform.ChildEnumerator;
        var entitiesToPreserve = new List<EntityUid>();

        while (enumerator.MoveNext(out var child))
        {
            FindEntitiesToPreserve(child, ref entitiesToPreserve);
        }
        foreach (var ent in entitiesToPreserve)
        {
            // Teleport this item and all its children to the floor (or space).
            _transform.SetCoordinates(ent, new EntityCoordinates(destination, 0, 0));
            _transform.AttachToGridOrMap(ent);
        }
    }

    // checks if something has the ShipyardPreserveOnSaleComponent and if it does, adds it to the list
    private void FindEntitiesToPreserve(EntityUid entity, ref List<EntityUid> output)
    {
        if (TryComp<ShipyardSellConditionComponent>(entity, out var comp) && comp.PreserveOnSale == true)
        {
            output.Add(entity);
            return;
        }
        if (TryComp<EntityStorageComponent>(entity, out var storageComp))
        {
            // Make storage containers delete their contents when they are deleted during ship sale
            storageComp.DeleteContentsOnDestruction = true;
            Dirty(entity, storageComp);
        }

        if (TryComp<ContainerManagerComponent>(entity, out var containers))
        {
            foreach (var container in containers.Containers.Values)
            {
                foreach (var ent in container.ContainedEntities)
                {
                    FindEntitiesToPreserve(ent, ref output);
                }
            }
        }
    }

    // returns false if it has ShipyardPreserveOnSaleComponent, true otherwise
    private bool LacksPreserveOnSaleComp(EntityUid uid)
    {
        return !TryComp<ShipyardSellConditionComponent>(uid, out var comp) || comp.PreserveOnSale == false;
    }
    private void CleanupShipyard()
    {
        if (ShipyardMap == null || !_map.MapExists(ShipyardMap.Value))
        {
            ShipyardMap = null;
            return;
        }

        _map.DeleteMap(ShipyardMap.Value);
    }

    public void SetupShipyardIfNeeded()
    {
        if (ShipyardMap != null && _map.MapExists(ShipyardMap.Value))
            return;

        _map.CreateMap(out var shipyardMap);
        ShipyardMap = shipyardMap;

        _map.SetPaused(ShipyardMap.Value, false);
    }

    // <summary>
    // Tries to rename a shuttle deed and update the respective components.
    // Returns true if successful.
    //
    // Null name parts are promptly ignored.
    // </summary>
    public bool TryRenameShuttle(EntityUid uid, ShuttleDeedComponent? shuttleDeed, string? newName, string? newSuffix)
    {
        if (!Resolve(uid, ref shuttleDeed))
            return false;

        var shuttle = shuttleDeed.ShuttleUid;
        if (shuttle != null
             && TryGetEntity(shuttle.Value, out var shuttleEntity)
             && _station.GetOwningStation(shuttleEntity.Value) is { Valid: true } shuttleStation)
        {
            // Update the primary deed
            shuttleDeed.ShuttleName = newName;
            shuttleDeed.ShuttleNameSuffix = newSuffix;
            Dirty(uid, shuttleDeed);

            // Find and update all other deeds for the same ship
            var query = EntityQueryEnumerator<ShuttleDeedComponent>();
            while (query.MoveNext(out var deedEntity, out var deed))
            {
                // Skip the deed we already updated
                if (deedEntity == uid)
                    continue;

                // Update deeds that reference the same shuttle
                if (deed.ShuttleUid == shuttle)
                {
                    deed.ShuttleName = newName;
                    deed.ShuttleNameSuffix = newSuffix;
                    Dirty(deedEntity, deed);
                }
            }

            var fullName = GetFullName(shuttleDeed);
            _station.RenameStation(shuttleStation, fullName, loud: false);
            _metaData.SetEntityName(shuttleEntity.Value, fullName);
            _metaData.SetEntityName(shuttleStation, fullName);
        }
        else
        {
            _sawmill.Error($"Could not rename shuttle {ToPrettyString(shuttle):entity} to {newName}");
            return false;
        }

        //TODO: move this to an event that others hook into.
        if (shuttleDeed.ShuttleUid != null &&
            _shuttleRecordsSystem.TryGetRecord(shuttleDeed.ShuttleUid.Value, out var record))
        {
            record.Name = newName ?? "";
            record.Suffix = newSuffix ?? "";
            _shuttleRecordsSystem.TryUpdateRecord(record);
        }

        return true;
    }

    /// <summary>
    /// Returns the full name of the shuttle component in the form of [prefix] [name] [suffix].
    /// </summary>
    public static string GetFullName(ShuttleDeedComponent comp)
    {
        string?[] parts = { comp.ShuttleName, comp.ShuttleNameSuffix };
        return string.Join(' ', parts.Where(it => it != null));
    }

    /// <summary>
    /// Attempts to extract ship name from YAML data
    /// </summary>
    private string? ExtractShipNameFromYaml(string yamlData)
    {
        try
        {
            // Simple YAML parsing to extract ship name
            var lines = yamlData.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("shipName:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        return parts[1].Trim().Trim('"', '\'');
                    }
                }
                // Also check for entity names that might indicate ship name
                if (trimmedLine.StartsWith("name:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        var name = parts[1].Trim().Trim('"', '\'');
                        // Only use if it looks like a ship name (not generic component names)
                        if (!name.Contains("Component") && !name.Contains("System") && name.Length > 3)
                        {
                            return name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to extract ship name from YAML: {ex}");
        }
    }
}
