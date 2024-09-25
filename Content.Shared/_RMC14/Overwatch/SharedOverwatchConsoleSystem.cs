﻿using System.Linq;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Marines.Squads;
using Content.Shared._RMC14.Roles;
using Content.Shared._RMC14.Rules;
using Content.Shared._RMC14.TacticalMap;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.IdentityManagement;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Popups;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._RMC14.Overwatch;

public abstract class SharedOverwatchConsoleSystem : EntitySystem
{
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedTacticalMapSystem _tacticalMap = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;

    private EntityQuery<ActorComponent> _actor;
    private EntityQuery<AlmayerComponent> _almayerQuery;
    private EntityQuery<MobStateComponent> _mobStateQuery;
    private EntityQuery<OriginalRoleComponent> _originalRoleQuery;
    private EntityQuery<RMCPlanetComponent> _planetQuery;

    private ProtoId<DamageGroupPrototype> _bruteGroup = "Brute";
    private ProtoId<DamageGroupPrototype> _burnGroup = "Burn";
    private ProtoId<DamageGroupPrototype> _toxinGroup = "Toxin";

    public override void Initialize()
    {
        _actor = GetEntityQuery<ActorComponent>();
        _almayerQuery = GetEntityQuery<AlmayerComponent>();
        _mobStateQuery = GetEntityQuery<MobStateComponent>();
        _originalRoleQuery = GetEntityQuery<OriginalRoleComponent>();
        _planetQuery = GetEntityQuery<RMCPlanetComponent>();

        SubscribeLocalEvent<OverwatchConsoleComponent, BoundUIOpenedEvent>(OnBUIOpened);

        SubscribeLocalEvent<OverwatchWatchingComponent, MoveInputEvent>(OnWatchingMoveInput);
        SubscribeLocalEvent<OverwatchWatchingComponent, DamageChangedEvent>(OnWatchingDamageChanged);

        SubscribeLocalEvent<SquadMemberComponent, SquadMemberUpdatedEvent>(OnSquadMemberUpdated);

        Subs.BuiEvents<OverwatchConsoleComponent>(OverwatchConsoleUI.Key, subs =>
        {
            subs.Event<OverwatchConsoleSelectSquadBuiMsg>(OnOverwatchSelectSquadBui);
            subs.Event<OverwatchViewTacticalMapBuiMsg>(OnOverwatchViewTacticalMapBui);
            subs.Event<OverwatchConsoleTakeOperatorBuiMsg>(OnOverwatchTakeOperatorBui);
            subs.Event<OverwatchConsoleStopOverwatchBuiMsg>(OnOverwatchStopBui);
            subs.Event<OverwatchConsoleSetLocationBuiMsg>(OnOverwatchSetLocationBui);
            subs.Event<OverwatchConsoleShowDeadBuiMsg>(OnOverwatchShowDeadBui);
            subs.Event<OverwatchConsoleShowHiddenBuiMsg>(OnOverwatchShowHiddenBui);
            subs.Event<OverwatchConsoleWatchBuiMsg>(OnOverwatchWatchBui);
            subs.Event<OverwatchConsoleHideBuiMsg>(OnOverwatchHideBui);
        });
    }

    private void OnBUIOpened(Entity<OverwatchConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (_net.IsClient)
            return;

        var state = GetOverwatchBuiState();
        _ui.SetUiState(ent.Owner, OverwatchConsoleUI.Key, state);
    }

    private void OnWatchingMoveInput(Entity<OverwatchWatchingComponent> ent, ref MoveInputEvent args)
    {
        if (!args.HasDirectionalMovement)
            return;

        TryLocalUnwatch(ent);
    }

    private void OnWatchingDamageChanged(Entity<OverwatchWatchingComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta is not { } delta)
            return;

        var damage = delta.GetDamagePerGroup(_prototypes);
        var bruteDamage = damage.GetValueOrDefault(_bruteGroup);
        var burnDamage = damage.GetValueOrDefault(_burnGroup);
        var toxinDamage = damage.GetValueOrDefault(_toxinGroup);
        if (bruteDamage + burnDamage <= FixedPoint2.Zero && toxinDamage <= 10)
            return;

        TryLocalUnwatch(ent);

        foreach (var (uiEnt, uiKey) in _ui.GetActorUis(ent.Owner).ToArray())
        {
            if (uiKey is OverwatchConsoleUI.Key)
                _ui.CloseUi(uiEnt, uiKey, ent);
        }

        if (_net.IsServer)
            _popup.PopupEntity("The pain kicked you out of the console!", ent, ent, PopupType.MediumCaution);
    }

    private void OnSquadMemberUpdated(Entity<SquadMemberComponent> ent, ref SquadMemberUpdatedEvent args)
    {
        var state = GetOverwatchBuiState();
        var consoles = EntityQueryEnumerator<OverwatchConsoleComponent>();
        while (consoles.MoveNext(out var uid, out _))
        {
            _ui.SetUiState(uid, OverwatchConsoleUI.Key, state);
        }
    }

    private void OnOverwatchSelectSquadBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleSelectSquadBuiMsg args)
    {
        if (_net.IsServer)
        {
            if (!TryGetEntity(args.Squad, out var squad) || !HasComp<SquadTeamComponent>(squad))
            {
                Log.Warning($"{ToPrettyString(args.Actor)} tried to select invalid squad id {ToPrettyString(squad)}");
                return;
            }
        }

        ent.Comp.Squad = args.Squad;
        ent.Comp.Operator = Identity.Name(args.Actor, EntityManager);
        Dirty(ent);
    }

    private void OnOverwatchViewTacticalMapBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchViewTacticalMapBuiMsg args)
    {
        _tacticalMap.OpenComputerMap(ent.Owner, args.Actor);
    }

    private void OnOverwatchTakeOperatorBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleTakeOperatorBuiMsg args)
    {
        ent.Comp.Operator = Identity.Name(args.Actor, EntityManager);
        Dirty(ent);
    }

    private void OnOverwatchStopBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleStopOverwatchBuiMsg args)
    {
        ent.Comp.Squad = null;
        ent.Comp.Operator = null;
        Dirty(ent);
    }

    private void OnOverwatchSetLocationBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleSetLocationBuiMsg args)
    {
        if (args.Location < OverwatchLocation.Min || args.Location > OverwatchLocation.Max)
            return;

        ent.Comp.Location = args.Location;
        Dirty(ent);
    }

    private void OnOverwatchShowDeadBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleShowDeadBuiMsg args)
    {
        ent.Comp.ShowDead = args.Show;
        Dirty(ent);
    }

    private void OnOverwatchShowHiddenBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleShowHiddenBuiMsg args)
    {
        ent.Comp.ShowHidden = args.Show;
        Dirty(ent);
    }

    private void OnOverwatchWatchBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleWatchBuiMsg args)
    {
        if (args.Target == default || !TryGetEntity(args.Target, out var target))
            return;

        Watch(args.Actor, target.Value);
    }

    private void OnOverwatchHideBui(Entity<OverwatchConsoleComponent> ent, ref OverwatchConsoleHideBuiMsg args)
    {
        if (_net.IsClient)
        {
            if (args.Hide)
                ent.Comp.Hidden.Add(args.Target);
            else
                ent.Comp.Hidden.Remove(args.Target);

            Dirty(ent);
            return;
        }

        if (args.Target == default || !TryGetEntity(args.Target, out var target))
            return;

        if (!HasComp<SquadMemberComponent>(target))
            return;

        if (args.Hide)
            ent.Comp.Hidden.Add(args.Target);
        else
            ent.Comp.Hidden.Remove(args.Target);

        Dirty(ent);

        var state = GetOverwatchBuiState();
        _ui.SetUiState(ent.Owner, OverwatchConsoleUI.Key, state);
    }

    protected virtual void Watch(Entity<ActorComponent?, EyeComponent?> watcher, Entity<OverwatchCameraComponent?> toWatch)
    {
    }

    protected virtual void Unwatch(Entity<EyeComponent?> watcher, ICommonSession player)
    {
        if (!Resolve(watcher, ref watcher.Comp))
            return;

        _eye.SetTarget(watcher, watcher, watcher);
    }

    private OverwatchConsoleBuiState GetOverwatchBuiState()
    {
        var squads = new List<OverwatchSquad>();
        var marines = new Dictionary<NetEntity, List<OverwatchMarine>>();
        var query = EntityQueryEnumerator<SquadTeamComponent>();
        while (query.MoveNext(out var uid, out var team))
        {
            var netUid = GetNetEntity(uid);
            squads.Add(new OverwatchSquad(netUid, Name(uid), team.Color));
            var members = marines.GetOrNew(netUid);

            foreach (var member in team.Members)
            {
                if (TerminatingOrDeleted(member))
                    continue;

                // to ignore cryo'd marines
                var xform = Transform(member);
                if (!_map.TryGetMap(xform.MapID, out var mapId) ||
                    _map.IsPaused(mapId.Value))
                {
                    continue;
                }

                var name = Identity.Name(member, EntityManager);

                _inventory.TryGetInventoryEntity<OverwatchCameraComponent>(member, out var camera);
                var mobState = _mobStateQuery.CompOrNull(member)?.CurrentState ?? MobState.Alive;
                var ssd = !_actor.HasComp(member);
                var role = _originalRoleQuery.CompOrNull(member)?.Job;
                var deployed = !_almayerQuery.HasComp(_transform.GetMap((member, xform)));
                var location = _planetQuery.HasComp(mapId) ? OverwatchLocation.Planet : OverwatchLocation.Ship;

                members.Add(new OverwatchMarine(
                    GetNetEntity(member),
                    GetNetEntity(camera),
                    name,
                    mobState,
                    ssd,
                    role,
                    deployed,
                    location
                ));
            }
        }

        return new OverwatchConsoleBuiState(squads, marines);
    }

    public bool IsHidden(Entity<OverwatchConsoleComponent> console, NetEntity marine)
    {
        return console.Comp.Hidden.Contains(marine);
    }

    private void TryLocalUnwatch(Entity<OverwatchWatchingComponent> ent)
    {
        if (_net.IsClient && _player.LocalEntity == ent.Owner && _player.LocalSession != null)
            Unwatch(ent.Owner, _player.LocalSession);
        else if (TryComp(ent, out ActorComponent? actor))
            Unwatch(ent.Owner, actor.PlayerSession);
    }
}