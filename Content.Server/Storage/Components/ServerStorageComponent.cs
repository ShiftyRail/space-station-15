using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.DoAfter;
using Content.Server.Hands.Components;
using Content.Server.Interaction;
using Content.Server.Items;
using Content.Shared.Acts;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Helpers;
using Content.Shared.Item;
using Content.Shared.Placeable;
using Content.Shared.Popups;
using Content.Shared.Sound;
using Content.Shared.Storage;
using Content.Shared.Whitelist;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Players;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Content.Server.Storage.Components
{
    /// <summary>
    /// Storage component for containing entities within this one, matches a UI on the client which shows stored entities
    /// </summary>
    [RegisterComponent]
    [ComponentReference(typeof(IActivate))]
    [ComponentReference(typeof(IStorageComponent))]
    public class ServerStorageComponent : SharedStorageComponent, IInteractUsing, IUse, IActivate, IStorageComponent, IDestroyAct, IExAct, IAfterInteract
    {
        private const string LoggerName = "Storage";

        private Container? _storage;
        private readonly Dictionary<IEntity, int> _sizeCache = new();

        [DataField("occludesLight")]
        private bool _occludesLight = true;

        [DataField("quickInsert")]
        private bool _quickInsert = false; // Can insert storables by "attacking" them with the storage entity

        [DataField("clickInsert")]
        private bool _clickInsert = true; // Can insert stuff by clicking the storage entity with it

        [DataField("areaInsert")]
        private bool _areaInsert = false;  // "Attacking" with the storage entity causes it to insert all nearby storables after a delay
        [DataField("areaInsertRadius")]
        private int _areaInsertRadius = 1;

        [DataField("whitelist")]
        private EntityWhitelist? _whitelist = null;

        private bool _storageInitialCalculated;
        private int _storageUsed;
        [DataField("capacity")]
        private int _storageCapacityMax = 10000;
        public readonly HashSet<IPlayerSession> SubscribedSessions = new();

        [DataField("storageSoundCollection")]
        public SoundSpecifier StorageSoundCollection { get; set; } = new SoundCollectionSpecifier("storageRustle");

        [ViewVariables]
        public override IReadOnlyList<IEntity>? StoredEntities => _storage?.ContainedEntities;

        [ViewVariables(VVAccess.ReadWrite)]
        public bool OccludesLight
        {
            get => _occludesLight;
            set
            {
                _occludesLight = value;
                if (_storage != null) _storage.OccludesLight = value;
            }
        }

        private void EnsureInitialCalculated()
        {
            if (_storageInitialCalculated)
            {
                return;
            }

            RecalculateStorageUsed();

            _storageInitialCalculated = true;
        }

        private void RecalculateStorageUsed()
        {
            _storageUsed = 0;

            if (_storage == null)
            {
                return;
            }

            foreach (var entity in _storage.ContainedEntities)
            {
                var item = entity.GetComponent<SharedItemComponent>();
                _storageUsed += item.Size;
            }
        }

        /// <summary>
        ///     Verifies if an entity can be stored and if it fits
        /// </summary>
        /// <param name="entity">The entity to check</param>
        /// <returns>true if it can be inserted, false otherwise</returns>
        public bool CanInsert(IEntity entity)
        {
            EnsureInitialCalculated();

            if (entity.TryGetComponent(out ServerStorageComponent? storage) &&
                storage._storageCapacityMax >= _storageCapacityMax)
            {
                return false;
            }

            if (entity.TryGetComponent(out SharedItemComponent? store) &&
                store.Size > _storageCapacityMax - _storageUsed)
            {
                return false;
            }

            if (_whitelist != null && !_whitelist.IsValid(entity.Uid))
            {
                return false;
            }

            if (entity.Transform.Anchored)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Inserts into the storage container
        /// </summary>
        /// <param name="entity">The entity to insert</param>
        /// <returns>true if the entity was inserted, false otherwise</returns>
        public bool Insert(IEntity entity)
        {
            return CanInsert(entity) && _storage?.Insert(entity) == true;
        }

        public override bool Remove(IEntity entity)
        {
            EnsureInitialCalculated();
            return _storage?.Remove(entity) == true;
        }

        public void HandleEntityMaybeInserted(EntInsertedIntoContainerMessage message)
        {
            if (message.Container != _storage)
            {
                return;
            }

            PlaySoundCollection();
            EnsureInitialCalculated();

            Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) had entity (UID {message.Entity.Uid}) inserted into it.");

            var size = 0;
            if (message.Entity.TryGetComponent(out SharedItemComponent? storable))
                size = storable.Size;

            _storageUsed += size;
            _sizeCache[message.Entity] = size;

            UpdateClientInventories();
        }

        public void HandleEntityMaybeRemoved(EntRemovedFromContainerMessage message)
        {
            if (message.Container != _storage)
            {
                return;
            }

            EnsureInitialCalculated();

            Logger.DebugS(LoggerName, $"Storage (UID {Owner}) had entity (UID {message.Entity}) removed from it.");

            if (!_sizeCache.TryGetValue(message.Entity, out var size))
            {
                Logger.WarningS(LoggerName, $"Removed entity {message.Entity} without a cached size from storage {Owner} at {Owner.Transform.MapPosition}");

                RecalculateStorageUsed();
                return;
            }

            _storageUsed -= size;

            UpdateClientInventories();
        }

        /// <summary>
        ///     Inserts an entity into storage from the player's active hand
        /// </summary>
        /// <param name="player">The player to insert an entity from</param>
        /// <returns>true if inserted, false otherwise</returns>
        public bool PlayerInsertHeldEntity(IEntity player)
        {
            EnsureInitialCalculated();

            if (!player.TryGetComponent(out HandsComponent? hands) ||
                hands.GetActiveHand == null)
            {
                return false;
            }

            var toInsert = hands.GetActiveHand;

            if (!hands.Drop(toInsert.Owner))
            {
                Owner.PopupMessage(player, "Can't insert.");
                return false;
            }

            if (!Insert(toInsert.Owner))
            {
                hands.PutInHand(toInsert);
                Owner.PopupMessage(player, "Can't insert.");
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Inserts an Entity (<paramref name="toInsert"/>) in the world into storage, informing <paramref name="player"/> if it fails.
        ///     <paramref name="toInsert"/> is *NOT* held, see <see cref="PlayerInsertHeldEntity(IEntity)"/>.
        /// </summary>
        /// <param name="player">The player to insert an entity with</param>
        /// <returns>true if inserted, false otherwise</returns>
        public bool PlayerInsertEntityInWorld(IEntity player, IEntity toInsert)
        {
            EnsureInitialCalculated();

            if (!Insert(toInsert))
            {
                Owner.PopupMessage(player, "Can't insert.");
                return false;
            }
            return true;
        }

        /// <summary>
        ///     Opens the storage UI for an entity
        /// </summary>
        /// <param name="entity">The entity to open the UI for</param>
        public void OpenStorageUI(IEntity entity)
        {
            PlaySoundCollection();
            EnsureInitialCalculated();

            var userSession = entity.GetComponent<ActorComponent>().PlayerSession;

            Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) \"used\" by player session (UID {userSession.AttachedEntityUid}).");

            SubscribeSession(userSession);
#pragma warning disable 618
            SendNetworkMessage(new OpenStorageUIMessage(), userSession.ConnectedClient);
#pragma warning restore 618
            UpdateClientInventory(userSession);
        }

        /// <summary>
        ///     Updates the storage UI on all subscribed actors, informing them of the state of the container.
        /// </summary>
        private void UpdateClientInventories()
        {
            foreach (var session in SubscribedSessions)
            {
                UpdateClientInventory(session);
            }
        }

        /// <summary>
        ///     Updates storage UI on a client, informing them of the state of the container.
        /// </summary>
        /// <param name="session">The client to be updated</param>
        private void UpdateClientInventory(IPlayerSession session)
        {
            if (session.AttachedEntity == null)
            {
                Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) detected no attached entity in player session (UID {session.AttachedEntityUid}).");

                UnsubscribeSession(session);
                return;
            }

            if (_storage == null)
            {
                Logger.WarningS(LoggerName, $"{nameof(UpdateClientInventory)} called with null {nameof(_storage)}");

                return;
            }

            if (StoredEntities == null)
            {
                Logger.WarningS(LoggerName, $"{nameof(UpdateClientInventory)} called with null {nameof(StoredEntities)}");

                return;
            }

            var stored = StoredEntities.Select(e => e.Uid).ToArray();

#pragma warning disable 618
            SendNetworkMessage(new StorageHeldItemsMessage(stored, _storageUsed, _storageCapacityMax), session.ConnectedClient);
#pragma warning restore 618
        }

        /// <summary>
        ///     Adds a session to the update list.
        /// </summary>
        /// <param name="session">The session to add</param>
        private void SubscribeSession(IPlayerSession session)
        {
            EnsureInitialCalculated();

            if (!SubscribedSessions.Contains(session))
            {
                Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) subscribed player session (UID {session.AttachedEntityUid}).");

                session.PlayerStatusChanged += HandlePlayerSessionChangeEvent;
                SubscribedSessions.Add(session);

                UpdateDoorState();
            }
        }

        /// <summary>
        ///     Removes a session from the update list.
        /// </summary>
        /// <param name="session">The session to remove</param>
        public void UnsubscribeSession(IPlayerSession session)
        {
            if (SubscribedSessions.Contains(session))
            {
                Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) unsubscribed player session (UID {session.AttachedEntityUid}).");

                SubscribedSessions.Remove(session);
#pragma warning disable 618
                SendNetworkMessage(new CloseStorageUIMessage(), session.ConnectedClient);
#pragma warning restore 618

                UpdateDoorState();
            }
        }

        private void HandlePlayerSessionChangeEvent(object? obj, SessionStatusEventArgs sessionStatus)
        {
            Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) handled a status change in player session (UID {sessionStatus.Session.AttachedEntityUid}).");

            if (sessionStatus.NewStatus != SessionStatus.InGame)
            {
                UnsubscribeSession(sessionStatus.Session);
            }
        }

        private void UpdateDoorState()
        {
            if (Owner.TryGetComponent(out AppearanceComponent? appearance))
            {
                appearance.SetData(StorageVisuals.Open, SubscribedSessions.Count != 0);
            }
        }

        protected override void Initialize()
        {
            base.Initialize();

            // ReSharper disable once StringLiteralTypo
            _storage = Owner.EnsureContainer<Container>("storagebase");
            _storage.OccludesLight = _occludesLight;
        }

        [Obsolete("Component Messages are deprecated, use Entity Events instead.")]
        public override void HandleNetworkMessage(ComponentMessage message, INetChannel channel, ICommonSession? session = null)
        {
            base.HandleNetworkMessage(message, channel, session);

            if (session == null)
            {
                throw new ArgumentException(nameof(session));
            }

            switch (message)
            {
                case RemoveEntityMessage remove:
                {
                    EnsureInitialCalculated();

                    var player = session.AttachedEntity;

                    if (player == null)
                    {
                        break;
                    }

                    var ownerTransform = Owner.Transform;
                    var playerTransform = player.Transform;

                    if (!playerTransform.Coordinates.InRange(Owner.EntityManager, ownerTransform.Coordinates, 2) ||
                        Owner.IsInContainer() && !playerTransform.ContainsEntity(ownerTransform))
                    {
                        break;
                    }

                    if (!Owner.EntityManager.TryGetEntity(remove.EntityUid, out var entity) || _storage?.Contains(entity) == false)
                    {
                        break;
                    }

                    if (!entity.TryGetComponent(out ItemComponent? item) || !player.TryGetComponent(out HandsComponent? hands))
                    {
                        break;
                    }

                    if (!hands.CanPutInHand(item))
                    {
                        break;
                    }

                    hands.PutInHand(item);

                    break;
                }
                case InsertEntityMessage _:
                {
                    EnsureInitialCalculated();

                    var player = session.AttachedEntity;

                    if (player == null)
                    {
                        break;
                    }

                    if (!player.InRangeUnobstructed(Owner, popup: true))
                    {
                        break;
                    }

                    PlayerInsertHeldEntity(player);

                    break;
                }
                case CloseStorageUIMessage _:
                {
                    if (session is not IPlayerSession playerSession)
                    {
                        break;
                    }

                    UnsubscribeSession(playerSession);
                    break;
                }
            }
        }

        /// <summary>
        /// Inserts storable entities into this storage container if possible, otherwise return to the hand of the user
        /// </summary>
        /// <param name="eventArgs"></param>
        /// <returns>true if inserted, false otherwise</returns>
        async Task<bool> IInteractUsing.InteractUsing(InteractUsingEventArgs eventArgs)
        {
            if (!_clickInsert)
                return false;
            Logger.DebugS(LoggerName, $"Storage (UID {Owner.Uid}) attacked by user (UID {eventArgs.User.Uid}) with entity (UID {eventArgs.Using.Uid}).");

            if (Owner.HasComponent<PlaceableSurfaceComponent>())
            {
                return false;
            }

            return PlayerInsertHeldEntity(eventArgs.User);
        }

        /// <summary>
        /// Sends a message to open the storage UI
        /// </summary>
        /// <param name="eventArgs"></param>
        /// <returns></returns>
        bool IUse.UseEntity(UseEntityEventArgs eventArgs)
        {
            EnsureInitialCalculated();
            OpenStorageUI(eventArgs.User);
            return false;
        }

        void IActivate.Activate(ActivateEventArgs eventArgs)
        {
#pragma warning disable 618
            ((IUse) this).UseEntity(new UseEntityEventArgs(eventArgs.User));
#pragma warning restore 618
        }

        /// <summary>
        /// Allows a user to pick up entities by clicking them, or pick up all entities in a certain radius
        /// arround a click.
        /// </summary>
        /// <param name="eventArgs"></param>
        /// <returns></returns>
        async Task<bool> IAfterInteract.AfterInteract(AfterInteractEventArgs eventArgs)
        {
            if (!eventArgs.InRangeUnobstructed(ignoreInsideBlocker: true, popup: true)) return false;

            // Pick up all entities in a radius around the clicked location.
            // The last half of the if is because carpets exist and this is terrible
            if (_areaInsert && (eventArgs.Target == null || !eventArgs.Target.HasComponent<SharedItemComponent>()))
            {
                var validStorables = new List<IEntity>();
                foreach (var entity in IoCManager.Resolve<IEntityLookup>().GetEntitiesInRange(eventArgs.ClickLocation, _areaInsertRadius, LookupFlags.None))
                {
                    if (entity.IsInContainer()
                        || entity == eventArgs.User
                        || !entity.HasComponent<SharedItemComponent>()
                        || !EntitySystem.Get<InteractionSystem>().InRangeUnobstructed(eventArgs.User, entity))
                        continue;
                    validStorables.Add(entity);
                }

                //If there's only one then let's be generous
                if (validStorables.Count > 1)
                {
                    var doAfterSystem = EntitySystem.Get<DoAfterSystem>();
                    var doAfterArgs = new DoAfterEventArgs(eventArgs.User, 0.2f * validStorables.Count, CancellationToken.None, Owner)
                    {
                        BreakOnStun = true,
                        BreakOnDamage = true,
                        BreakOnUserMove = true,
                        NeedHand = true,
                    };
                    var result = await doAfterSystem.WaitDoAfter(doAfterArgs);
                    if (result != DoAfterStatus.Finished) return true;
                }

                var successfullyInserted = new List<EntityUid>();
                var successfullyInsertedPositions = new List<EntityCoordinates>();
                foreach (var entity in validStorables)
                {
                    // Check again, situation may have changed for some entities, but we'll still pick up any that are valid
                    if (entity.IsInContainer()
                        || entity == eventArgs.User
                        || !entity.HasComponent<SharedItemComponent>())
                        continue;
                    var position = EntityCoordinates.FromMap(Owner.Transform.Parent?.Owner ?? Owner, entity.Transform.MapPosition);
                    if (PlayerInsertEntityInWorld(eventArgs.User, entity))
                    {
                        successfullyInserted.Add(entity.Uid);
                        successfullyInsertedPositions.Add(position);
                    }
                }

                // If we picked up atleast one thing, play a sound and do a cool animation!
                if (successfullyInserted.Count > 0)
                {
                    PlaySoundCollection();
#pragma warning disable 618
                    SendNetworkMessage(
#pragma warning restore 618
                        new AnimateInsertingEntitiesMessage(
                            successfullyInserted,
                            successfullyInsertedPositions
                        )
                    );
                }
                return true;
            }
            // Pick up the clicked entity
            else if (_quickInsert)
            {
                if (eventArgs.Target == null
                    || eventArgs.Target.IsInContainer()
                    || eventArgs.Target == eventArgs.User
                    || !eventArgs.Target.HasComponent<SharedItemComponent>())
                    return false;
                var position = EntityCoordinates.FromMap(Owner.Transform.Parent?.Owner ?? Owner, eventArgs.Target.Transform.MapPosition);
                if (PlayerInsertEntityInWorld(eventArgs.User, eventArgs.Target))
                {
#pragma warning disable 618
                    SendNetworkMessage(new AnimateInsertingEntitiesMessage(
#pragma warning restore 618
                        new List<EntityUid>() { eventArgs.Target.Uid },
                        new List<EntityCoordinates>() { position }
                    ));
                    return true;
                }
                return true;
            }
            return false;
        }

        void IDestroyAct.OnDestroy(DestructionEventArgs eventArgs)
        {
            var storedEntities = StoredEntities?.ToList();

            if (storedEntities == null)
            {
                return;
            }

            foreach (var entity in storedEntities)
            {
                Remove(entity);
            }
        }

        void IExAct.OnExplosion(ExplosionEventArgs eventArgs)
        {
            if (eventArgs.Severity < ExplosionSeverity.Heavy)
            {
                return;
            }

            var storedEntities = StoredEntities?.ToList();

            if (storedEntities == null)
            {
                return;
            }

            foreach (var entity in storedEntities)
            {
                var exActs = entity.GetAllComponents<IExAct>().ToArray();
                foreach (var exAct in exActs)
                {
                    exAct.OnExplosion(eventArgs);
                }
            }
        }

        private void PlaySoundCollection()
        {
            SoundSystem.Play(Filter.Pvs(Owner), StorageSoundCollection.GetSound(), Owner, AudioParams.Default);
        }
    }
}
