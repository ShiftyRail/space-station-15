using System;
using Content.Server.Solar.Components;
using Content.Server.UserInterface;
using Content.Shared.Solar;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Server.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.Server.Solar.EntitySystems
{
    /// <summary>
    /// Responsible for updating solar control consoles.
    /// </summary>
    [UsedImplicitly]
    internal sealed class PowerSolarControlConsoleSystem : EntitySystem
    {
        [Dependency] private PowerSolarSystem _powerSolarSystem = default!;

        /// <summary>
        /// Timer used to avoid updating the UI state every frame (which would be overkill)
        /// </summary>
        private float _updateTimer;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<SolarControlConsoleComponent, ServerBoundUserInterfaceMessage>(OnUIMessage);
        }

        public override void Update(float frameTime)
        {
            _updateTimer += frameTime;
            if (_updateTimer >= 1)
            {
                _updateTimer -= 1;
                var state = new SolarControlConsoleBoundInterfaceState(_powerSolarSystem.TargetPanelRotation, _powerSolarSystem.TargetPanelVelocity, _powerSolarSystem.TotalPanelPower, _powerSolarSystem.TowardsSun);
                foreach (var component in EntityManager.EntityQuery<SolarControlConsoleComponent>())
                {
                    component.Owner.GetUIOrNull(SolarControlConsoleUiKey.Key)?.SetState(state);
                }
            }
        }
 
        private void OnUIMessage(EntityUid uid, SolarControlConsoleComponent component, ServerBoundUserInterfaceMessage obj)
        {
            if (component.Deleted) return;
            switch (obj.Message)
            {
                case SolarControlConsoleAdjustMessage msg:
                    if (double.IsFinite(msg.Rotation))
                    {
                        _powerSolarSystem.TargetPanelRotation = msg.Rotation.Reduced();
                    }
                    if (double.IsFinite(msg.AngularVelocity))
                    {
                        var degrees = msg.AngularVelocity.Degrees;
                        degrees = Math.Clamp(degrees, -PowerSolarSystem.MaxPanelVelocityDegrees, PowerSolarSystem.MaxPanelVelocityDegrees);
                        _powerSolarSystem.TargetPanelVelocity = Angle.FromDegrees(degrees);
                    }
                    break;
            }
        }

    }
}
