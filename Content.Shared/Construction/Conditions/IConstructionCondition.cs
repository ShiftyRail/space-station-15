﻿using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Shared.Construction.Conditions
{
    public interface IConstructionCondition
    {
        ConstructionGuideEntry? GenerateGuideEntry();
        bool Condition(IEntity user, EntityCoordinates location, Direction direction);
    }
}
