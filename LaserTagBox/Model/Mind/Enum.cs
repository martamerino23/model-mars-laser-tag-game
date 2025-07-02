// File: Enums.cs
using System; // Ensure System is included for basic types if needed

namespace LaserTagBox.Model.Mind
{

    // Actions the agent can take
    public enum PlayerAction // Moved from JeMaFeLearning
    {
        MoveRandom,
        MoveToEnemy,
        MoveToHill,
        MoveToDitch,
        MoveToOwnBase,
        MoveToEnemyFlag,
        AttackEnemy,
        Reload,
        ChangeStanceLying,
        ChangeStanceKneeling,
        ChangeStanceStanding,
        EvadeEnemy,
        ExplodeBarrel
    }
}