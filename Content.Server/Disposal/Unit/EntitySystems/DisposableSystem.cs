using System.Linq;
﻿using Content.Server.Atmos;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Disposal.Tube.Components;
using Content.Server.Disposal.Tube;
using Content.Server.Disposal.Unit.Components;
using JetBrains.Annotations;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Content.Server.Disposal.Unit.EntitySystems
{
    [UsedImplicitly]
    internal sealed class DisposableSystem : EntitySystem
    {
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly DisposalUnitSystem _disposalUnitSystem = default!;
        [Dependency] private readonly DisposalTubeSystem _disposalTubeSystem = default!;

        public void ExitDisposals(EntityUid uid, DisposalHolderComponent? holder = null, TransformComponent? holderTransform = null)
        {
            if (!Resolve(uid, ref holder, ref holderTransform))
                return;

            // Check for a disposal unit to throw them into and then eject them from it.
            // *This ejection also makes the target not collide with the unit.*
            // *This is on purpose.*
            var grid = _mapManager.GetGrid(holderTransform.GridID);
            var gridTileContents = grid.GetLocal(holderTransform.Coordinates);
            DisposalUnitComponent? duc = null;
            foreach (var contentUid in gridTileContents)
            {
                if (EntityManager.TryGetComponent(contentUid, out duc))
                    break;
            }

            foreach (var entity in holder.Container.ContainedEntities.ToArray())
            {
                if (entity.TryGetComponent(out IPhysBody? physics))
                {
                    physics.CanCollide = true;
                }

                holder.Container.ForceRemove(entity);

                if (entity.Transform.Parent == holderTransform)
                {
                    if (duc != null)
                    {
                        // Insert into disposal unit
                        entity.Transform.Coordinates = new EntityCoordinates(duc.OwnerUid, Vector2.Zero);
                        duc.Container.Insert(entity);
                    }
                    else
                    {
                        entity.Transform.AttachParentToContainerOrGrid();
                    }
                }
            }

            if (duc != null)
            {
                _disposalUnitSystem.TryEjectContents(duc);
            }

            var atmosphereSystem = EntitySystem.Get<AtmosphereSystem>();

            if (atmosphereSystem.GetTileMixture(holderTransform.Coordinates, true) is {} environment)
            {
                atmosphereSystem.Merge(environment, holder.Air);
                holder.Air.Clear();
            }

            EntityManager.DeleteEntity(uid);
        }

        // Note: This function will cause an ExitDisposals on any failure that does not make an ExitDisposals impossible.
        public bool EnterTube(EntityUid holderUid, EntityUid toUid, DisposalHolderComponent? holder = null, TransformComponent? holderTransform = null, IDisposalTubeComponent? to = null, TransformComponent? toTransform = null)
        {
            if (!Resolve(holderUid, ref holder, ref holderTransform))
                return false;
            if (!Resolve(toUid, ref to, ref toTransform))
            {
                ExitDisposals(holderUid, holder, holderTransform);
                return false;
            }

            // Insert into next tube
            holderTransform.Coordinates = new EntityCoordinates(toUid, Vector2.Zero);
            if (!to.Contents.Insert(holder.Owner))
            {
                ExitDisposals(holderUid, holder, holderTransform);
                return false;
            }

            if (holder.CurrentTube != null)
            {
                holder.PreviousTube = holder.CurrentTube;
                holder.PreviousDirection = holder.CurrentDirection;
            }
            var dir = to.NextDirection(holder);
            // Invalid direction = exit now!
            if (dir == Direction.Invalid)
            {
                ExitDisposals(holderUid, holder, holderTransform);
                return false;
            }

            holderTransform.Coordinates = toTransform.Coordinates;
            holder.CurrentTube = to;
            holder.CurrentDirection = dir;
            holder.StartingTime = 0.1f;
            holder.TimeLeft = 0.1f;
            return true;
        }

        public override void Update(float frameTime)
        {
            foreach (var comp in EntityManager.EntityQuery<DisposalHolderComponent>())
            {
                UpdateComp(comp, frameTime);
            }
        }

        private void UpdateComp(DisposalHolderComponent holder, float frameTime)
        {
            while (frameTime > 0)
            {
                var time = frameTime;
                if (time > holder.TimeLeft)
                {
                    time = holder.TimeLeft;
                }

                holder.TimeLeft -= time;
                frameTime -= time;

                var currentTube = holder.CurrentTube;
                if (currentTube == null || currentTube.Deleted)
                {
                    ExitDisposals(holder.OwnerUid);
                    break;
                }

                if (holder.TimeLeft > 0)
                {
                    var progress = 1 - holder.TimeLeft / holder.StartingTime;
                    var origin = currentTube.Owner.Transform.Coordinates;
                    var destination = holder.CurrentDirection.ToVec();
                    var newPosition = destination * progress;

                    holder.Owner.Transform.Coordinates = origin.Offset(newPosition);

                    continue;
                }

                // Past this point, we are performing inter-tube transfer!
                // Remove current tube content
                currentTube.Contents.ForceRemove(holder.Owner);

                // Find next tube
                var nextTube = _disposalTubeSystem.NextTubeFor(currentTube.OwnerUid, holder.CurrentDirection);
                if (nextTube == null || nextTube.Deleted)
                {
                    ExitDisposals(holder.OwnerUid);
                    break;
                }

                // Perform remainder of entry process
                if (!EnterTube(holder.OwnerUid, nextTube.OwnerUid, holder, null, nextTube, null))
                {
                    ExitDisposals(holder.OwnerUid);
                    break;
                }
            }
        }
    }
}
