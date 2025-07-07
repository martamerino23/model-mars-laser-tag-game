using System;
using System.Collections.Generic;
using System.Linq;
using LaserTagBox.Model.Body;
using LaserTagBox.Model.Shared;
using Mars.Common.Core.Random;
using Mars.Interfaces.Environments;
using System.IO;
using SystemTextJson = System.Text.Json; 
using LiteDB; 
namespace LaserTagBox.Model.Mind;

public class TeamJemaFe : AbstractPlayerMind
{
    public enum AgentRole
    {
        Attacker, Defender, Collector
    }
    public enum PlayerAction
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
  
    private JeMaFeLearning _qlearning;
    private PlayerMindLayer _mindLayer;
    private Position _goal;
    private Position _initPo;
    private AgentRole _role;
    private Color _ourColor;
    private readonly AgentRole _agentRole;

    private static int _globalAgentCounter;
    private int _myAgentIndex;
    
    public override void Init(PlayerMindLayer mindLayer)
    {
        _mindLayer = mindLayer;
        _initPo = Body.Position.Copy();

        _myAgentIndex = _globalAgentCounter++;

        if (_myAgentIndex % 3 == 0)
        {
            _role = AgentRole.Defender;
            _qlearning = new JeMaFeLearning();
            _qlearning.Init(mindLayer, (PlayerBody)this.Body);
        }
        else
        {
            switch (_myAgentIndex)
            {
                case var n when n % 3 == 1: _role = AgentRole.Collector; break;
                case var n when n % 3 == 2: _role = AgentRole.Collector; break;
                default: _role = AgentRole.Defender; break;
            }
        }

        Console.WriteLine($"Agent {_myAgentIndex}: {(_qlearning != null ? "Q-Learning" : "RuleBased")} Role: {_role}");
    }
    
    public override void Tick()
    {
        if (_qlearning != null)
        {
            _qlearning.Tick();
            return;
        }

        var enemies = Body.ExploreEnemies1() ?? new List<EnemySnapshot>();
        var ditches = Body.ExploreDitches1() ?? new List<Position>();
        var hills = Body.ExploreHills1() ?? new List<Position>();
        var barrels = Body.ExploreExplosiveBarrels1() ?? new List<Position>();
        var ownBase = Body.ExploreOwnFlagStand();
        var enemyFlags = Body.ExploreEnemyFlagStands1();

        //To know our team color
        var flags = Body.ExploreFlags2() ?? new List<FlagSnapshot>();
        var ourFlag = flags.FirstOrDefault(flag => GetDistance(flag.Position, ownBase) == 0);
        if (!ourFlag.Equals(default(FlagSnapshot)))
        {
            if (ourFlag.Team == Color.Blue)
            {
                _ourColor = Color.Blue;
            }
            else if (ourFlag.Team == Color.Red)
            {
                _ourColor = Color.Red;
            }
            else if (ourFlag.Team == Color.Yellow)
            {
                _ourColor = Color.Yellow;
            }
            else
            {
                _ourColor = Color.Blue;
            }
        }

        if (Body.ActionPoints < 2) return;

        switch (_role)
        {
            case AgentRole.Attacker:
                AttackerBehavior(ownBase, enemyFlags, enemies, ditches, hills, barrels);
                break;
            case AgentRole.Defender:
                DefenderBehavior(ownBase, enemyFlags, enemies, ditches, hills, barrels);
                break;
            case AgentRole.Collector:
                FlagCarrierBehavior(ownBase, enemyFlags, enemies, ditches, hills, barrels);
                break;
        }
    }
    
    #region Behaviors

    private void DefenderBehavior(Position ownBase, List<Position> enemyFlags, List<EnemySnapshot> enemies,
        List<Position> ditches, List<Position> hills, List<Position> barrels)
    {
        if (enemies == null) enemies = new List<EnemySnapshot>();

        Position myPosition = Body.Position;

        //Do nothing because we have too little action points
        if (Body.ActionPoints < 2)
        {
            return;
        }

        if (Body.Stance == Stance.Lying)
        {
            Body.ChangeStance2(Stance.Standing);
        }


        if (EnemyIsNear(enemies) || Body.WasTaggedLastTick)
        {
            var closeEnemies = enemies.Where(e => GetDistance(e.Position, myPosition) <= 5).ToList();

            if (CanShoot())
            {
                AttackEnemy(closeEnemies);
                Console.WriteLine("Defender: Defending!");
                return;
            }

            if (Body.Energy < 30)
            {
                Evade(enemies, ditches, hills);
                return;
            }

            Body.Reload3();
            return;
        }

        if (ownBase != null)
        {
            var flagThreats = enemies
                .Where(e => GetDistance(e.Position, ownBase) <= 5)
                .OrderBy(e => GetDistance(e.Position, ownBase))
                .ToList();

            if (flagThreats.Any())
            {
                Console.WriteLine("Defender: Enemies are near!");

                if (Body.Stance == Stance.Standing)
                {
                    Body.ChangeStance2(Stance.Lying);
                }

                if (CanShoot())
                {
                    AttackEnemy(flagThreats);
                    Console.WriteLine("Defender: Defending!");
                    return;
                }

                if (Body.Energy < 20)
                {
                    Evade(flagThreats, ditches, hills);
                    return;
                }

                Body.Reload3();
                return;
            }
        }

        if (!EnemyIsNear(enemies))
        {
            var patrolRange = 5;
            var patrolZone = GetRandomNearbyPosition(ownBase, patrolRange);
            _goal = patrolZone;
            Console.WriteLine("Defender: Patroling!");
            Body.GoTo(_goal);
        }

        if (!CanShoot())
        {
            Body.Reload3();
        }
    }

    private void AttackerBehavior(Position ownBase, List<Position> enemyFlags, List<EnemySnapshot> enemies,
        List<Position> ditches, List<Position> hills, List<Position> barrels)
    {
        enemyFlags ??= new List<Position>();
        enemies ??= new List<EnemySnapshot>();
        ditches ??= new List<Position>();
        hills ??= new List<Position>();
        barrels ??= new List<Position>();
        bool iHaveFlag = Body.CarryingFlag;
        var flags = Body.ExploreFlags2() ?? new List<FlagSnapshot>();
        var takenEnemiesFlag = flags.Where(flag => flag.PickedUp && flag.Team != _ourColor).FirstOrDefault();
        bool teamHasFlag = false;
        var teamNearBy = Body.ExploreTeam().Where(t => GetDistance(t.Position, Body.Position) <= Body.VisualRange)
            .FirstOrDefault();
        var enemiesNearby = getNearEnemies(enemies ?? new List<EnemySnapshot>());

        if (!takenEnemiesFlag.Equals(default(FlagSnapshot)) && takenEnemiesFlag.Team != _ourColor)
        {
            teamHasFlag = true;
        }

        if (Body.ActionPoints < 2)
        {
            return;
        }

        if (Body.Stance != Stance.Standing)
        {
            Body.ChangeStance2(Stance.Standing);
        }


        if (Body.WasTaggedLastTick)
        {
            if (CanShoot())
            {
                AttackEnemy(enemies);
                return;
            }
            else
            {
                Body.Reload3();
                AttackEnemy(enemiesNearby);
                return;
            }
        }

        if (!teamHasFlag && !iHaveFlag)
        {
            if (EnemyIsNear(enemies))
            {
                if (Body.Energy < 20)
                {
                    Evade(enemies, ditches, hills);
                    return;
                }

                if (CanShoot())
                {
                    if (TryExplodeBarrel(enemiesNearby, barrels))
                    {
                        return;
                    }

                    AttackEnemy(enemies);
                    Console.WriteLine("Attacking!");
                    return;
                }
                else
                {
                    Body.Reload3();
                    AttackEnemy(enemiesNearby);
                    return;
                }
            }

            if (enemyFlags.Any())
            {
                var enemyFlag = enemyFlags.OrderBy(f => Body.GetDistance(f)).First();
                _goal = enemyFlag;

                if (Body.Stance == Stance.Lying)
                {
                    Body.ChangeStance2(Stance.Standing);
                }

                Body.GoTo(_goal);
                Console.WriteLine("Attacker: Going to the enemy flag!");
            }

        }

        //FLAG TAKEN
        if (teamHasFlag && !iHaveFlag)
        {

            var moved = false;
            if (Body.Stance == Stance.Lying)
            {
                Body.ChangeStance2(Stance.Standing);
            }

            if (enemiesNearby.Any())
            {
                if (Body.Energy < 20)
                {
                    Evade(enemiesNearby, ditches, hills);
                    return;
                }

                if (CanShoot())
                {
                    if (TryExplodeBarrel(enemiesNearby, barrels))
                    {
                        return;
                    }

                    AttackEnemy(enemiesNearby);
                    Console.WriteLine("Attacking!");
                    return;
                }
                else
                {
                    Body.Reload3();
                    AttackEnemy(enemiesNearby);
                    return;
                }
            }

            if (!teamNearBy.Equals(default(FriendSnapshot)) && enemiesNearby.Any())
            {
                Console.WriteLine("Attacker: Going to help the team!");
                _goal = teamNearBy.Position;
                return;
            }


            if (_goal == null || !_goal.Equals(ownBase))
            {
                _goal = ownBase;
            }

            moved = Body.GoTo(_goal);
            if (!moved)
            {
                _goal = GetRandomPosition();
            }

        }

        if (iHaveFlag)
        {
            var moved = false;
            if (Body.Stance == Stance.Lying)
            {
                Body.ChangeStance2(Stance.Standing);
            }

            if (_goal == null || !_goal.Equals(ownBase))
            {
                _goal = ownBase;
                moved = Body.GoTo(_goal);
                if (!moved)
                {
                    _goal = GetRandomPosition();
                }

            }

            if (enemiesNearby.Any())
            {
                if (Body.Energy < 25)
                {
                    Evade(enemiesNearby, ditches, hills);
                    return;
                }

                if (CanShoot())
                {
                    if (TryExplodeBarrel(enemiesNearby, barrels))
                    {
                        return;
                    }

                    AttackEnemy(enemiesNearby);
                    return;
                }
                else
                {
                    Body.Reload3();
                    AttackEnemy(enemiesNearby);
                    return;
                }
            }

            if (_goal == null || !_goal.Equals(ownBase))
            {
                _goal = ownBase;
            }

            //To know if we bring an enemy flag back
            if (Body.GetDistance(ownBase) <= 1)
            {
                Console.WriteLine("Attacker: The flag from the enemy is near our base!");
                _goal = null;
                return;
            }

            moved = Body.GoTo(_goal);
            if (!moved)
            {
                _goal = GetRandomPosition();
            }

        }

    }

    private void FlagCarrierBehavior(Position ownBase, List<Position> enemyFlags, List<EnemySnapshot> enemies,
        List<Position> ditches, List<Position> hills, List<Position> barrels)
    {
        enemies ??= new List<EnemySnapshot>();
        ditches ??= new List<Position>();
        hills ??= new List<Position>();
        barrels ??= new List<Position>();
        enemyFlags ??= new List<Position>();
        bool flagIsTaken = Body.CarryingFlag;


        if (Body.ActionPoints < 2)
        {
            return;
        }

        if (Body.WasTaggedLastTick)
        {
            if (CanShoot())
            {
                AttackEnemy(enemies);
                return;
            }
            else
            {
                Body.Reload3();
            }
        }


        //FLAG NOT TAKEN
        if (!flagIsTaken)
        {
            if (enemyFlags.Any())
            {
                var enemyFlag = enemyFlags.OrderBy(f => Body.GetDistance(f)).First();
                _goal = enemyFlag;

                if (Body.Stance == Stance.Lying)
                {
                    Body.ChangeStance2(Stance.Standing);
                }

                Body.GoTo(_goal);
            }

            if (EnemyIsNear(enemies))
            {
                if (Body.Energy < 35)
                {
                    Evade(enemies, ditches, hills);
                    return;
                }

                if (CanShoot())
                {
                    AttackEnemy(enemies);
                    return;
                }
                else
                {
                    Body.Reload3();
                    return;
                }
            }

            if (!CanShoot())
            {
                Body.Reload3();
                return;
            }
        }

        //FLAG TAKEN
        if (flagIsTaken)
        {
            var enemiesNearby = getNearEnemies(enemies);
            var moved = false;
            if (Body.Stance == Stance.Lying)
            {
                Body.ChangeStance2(Stance.Standing);
            }

            if (_goal == null || !_goal.Equals(ownBase))
            {
                _goal = ownBase;
                moved = Body.GoTo(_goal);
                if (!moved)
                {
                    _goal = GetRandomPosition();
                }

            }

            if (enemiesNearby.Any())
            {
                if (Body.Energy < 35)
                {
                    Evade(enemiesNearby, ditches, hills);
                    return;
                }

                if (CanShoot())
                {
                    if (TryExplodeBarrel(enemiesNearby, barrels))
                    {
                        return;
                    }

                    AttackEnemy(enemiesNearby);
                    return;
                }
                else
                {
                    Body.Reload3();
                    AttackEnemy(enemiesNearby);
                    return;
                }
            }

            if (Body.Position.Equals(ownBase))
            {
                Console.WriteLine("The flag from the enemy is in our base!");
                _goal = null;
                return;
            }

            if (_goal == null || !_goal.Equals(ownBase))
            {
                _goal = ownBase;
            }

            moved = Body.GoTo(_goal);
            if (!moved)
            {
                _goal = GetRandomPosition();
            }

        }
    }
    
    #endregion

    #region Helper Methods
    private void MoveToGoal()
    {
        if (_goal == null)
        {
            _goal = GetRandomPosition();
        }

        if (Body.Stance == Stance.Lying)
        {
            Body.ChangeStance2(Stance.Standing);
        }

        if (Body.ActionPoints < 2) return;

        var moved = Body.GoTo(_goal);
        if (!moved)
        {
            for (int i = 0; i < 3; i++)
            {
                _goal = GetRandomPosition();
                if (Body.ActionPoints < 2) return;
                moved = Body.GoTo(_goal);
                if (moved) break;
            }

            if (!moved)
            {
                _goal = null;
            }
        }
    }

    private Position GetRandomPosition()
    {
        const int MAX_ATTEMPTS = 5;
        for (int i = 0; i < MAX_ATTEMPTS; i++)
        {
            var newX = RandomHelper.Random.Next(_mindLayer.Width);
            var newY = RandomHelper.Random.Next(_mindLayer.Height);
            var pos = Position.CreatePosition(newX, newY);

            if (IsValidPosition2(pos))
            {
                return pos;
            }
        }

        return Body.Position;
    }

    private bool IsValidPosition2(Position pos)
    {
        return pos.X >= 0 && pos.X < _mindLayer.Width &&
               pos.Y >= 0 && pos.Y < _mindLayer.Height &&
               Body.HasBeeline1(pos);
    }

    private Position GetRandomNearbyPosition(Position center, int range)
    {
        for (int i = 0; i < 5; i++)
        {
            int dx = RandomHelper.Random.Next(-range, range + 1);
            int dy = RandomHelper.Random.Next(-range, range + 1);
            var pos = Position.CreatePosition(center.X + dx, center.Y + dy);

            if (Body.HasBeeline1(pos)) return pos;
        }

        return center;
    }

    private int GetDistance(Position a, Position b)
    {
        return (int)(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y));
    }

    private bool TryExplodeBarrel(List<EnemySnapshot> enemies, List<Position> barrels)
    {
        foreach (var barrel in barrels)
        {
            if (enemies.Any(e => GetDistance(barrel, e.Position) <= 2))
            {
                Body.ChangeStance2(Stance.Lying);
                Body.Tag5(barrel);
                return true;
            }
        }

        return false;
    }

    private void AttackEnemy(List<EnemySnapshot> enemies)
    {
        Body.ChangeStance2(Stance.Lying);
        var targets = enemies
            .Where(e => Body.HasBeeline1(e.Position) && Body.GetDistance(e.Position) <= Body.VisualRange)
            .OrderBy(e => Body.GetDistance(e.Position)).ToList();

        if (!enemies.Any())
        {
            Console.WriteLine("No enemies to attack!");
            return;
        }


        if (targets.Any())
        {
            var target = targets.First();
            var distance = Body.GetDistance(target.Position);

            if (distance <= 4)
            {
                Body.ChangeStance2(Stance.Lying);
            }
            else if (distance <= 7)
            {
                Body.ChangeStance2(Stance.Kneeling);
            }

            _goal = target.Position.Copy();

            if (CanShoot() && Body.GetDistance(target.Position) <= Body.VisualRange
                           && Body.HasBeeline1(target.Position) && Body.ActionPoints >= 5)
            {
                Body.Tag5(target.Position);
            }
            else
            {
                Body.Reload3();
            }
        }
    }

    private void Evade(List<EnemySnapshot> enemies, List<Position> ditches, List<Position> hills)
    {
        var immediateThreats = enemies
            .Where(e => Body.GetDistance(e.Position) <= 3)
            .OrderBy(e => Body.GetDistance(e.Position))
            .ToList();


        var cover = SelectBestCover(hills, ditches, enemies);
        _goal = cover;


        if (immediateThreats.Any())
        {

            if (Body.Stance != Stance.Standing)
            {
                Body.ChangeStance2(Stance.Standing);
            }


            if (_mindLayer.GetCurrentTick() % 2 == 0)
            {
                _goal = GetZigzagPosition(immediateThreats.First().Position);
            }
        }
        else
        {

            if (Body.GetDistance(_goal) <= 2)
            {
                Body.ChangeStance2(Stance.Lying);
            }
            else if (Body.Stance != Stance.Standing)
            {
                Body.ChangeStance2(Stance.Standing);
            }
        }


        if (Body.ActionPoints >= 2)
        {
            if (!Body.GoTo(_goal))
            {
                _goal = FindAlternativeCover(hills, ditches, enemies);
                Body.GoTo(_goal);
            }
        }
    }

    private Position SelectBestCover(List<Position> hills, List<Position> ditches, List<EnemySnapshot> enemies)
    {
        var allCover = hills.Concat(ditches)
            .Where(c => !enemies.Any(e => GetDistance(e.Position, c) < 3))
            .Select(c => new
            {
                Position = c,
                Distance = Body.GetDistance(c),
                EnemyDistance = enemies.Any() ? enemies.Min(e => GetDistance(e.Position, c)) : 999,
                IsDitch = ditches.Contains(c)
            })
            .OrderBy(c => c.Distance)
            .ThenByDescending(c => c.EnemyDistance)
            .ThenBy(c => c.IsDitch)
            .FirstOrDefault();

        return allCover?.Position ?? GetEmergencyEscapePosition(enemies);
    }

    private Position GetZigzagPosition(Position enemyPos)
    {
        var angle = (_mindLayer.GetCurrentTick() % 4) * (Math.PI / 2);
        var distance = 3;

        var deltaX = (int)(Math.Cos(angle) * distance);
        var deltaY = (int)(Math.Sin(angle) * distance);

        if (Body.GetDistance(enemyPos) <
            Body.GetDistance(Position.CreatePosition(Body.Position.X + deltaX, Body.Position.Y + deltaY)))
        {
            deltaX *= -1;
            deltaY *= -1;
        }

        var newX = Math.Max(0, Math.Min(_mindLayer.Width - 1, Body.Position.X + deltaX));
        var newY = Math.Max(0, Math.Min(_mindLayer.Height - 1, Body.Position.Y + deltaY));

        return Position.CreatePosition(newX, newY);
    }

    private Position GetEmergencyEscapePosition(List<EnemySnapshot> enemies)
    {
        if (!enemies.Any()) return Body.Position;

        var averageEnemyX = enemies.Average(e => e.Position.X);
        var averageEnemyY = enemies.Average(e => e.Position.Y);

        var directionX = Body.Position.X - averageEnemyX;
        var directionY = Body.Position.Y - averageEnemyY;

        var length = Math.Sqrt(directionX * directionX + directionY * directionY);
        var escapeDistance = 5;

        var newX = Body.Position.X + (int)((directionX / length) * escapeDistance);
        var newY = Body.Position.Y + (int)((directionY / length) * escapeDistance);

        newX = Math.Max(0, Math.Min(_mindLayer.Width - 1, newX));
        newY = Math.Max(0, Math.Min(_mindLayer.Height - 1, newY));

        return Position.CreatePosition(newX, newY);
    }

    private Position FindAlternativeCover(List<Position> hills, List<Position> ditches, List<EnemySnapshot> enemies)
    {
        hills ??= new List<Position>();
        ditches ??= new List<Position>();
        enemies ??= new List<EnemySnapshot>();

        var safeHills = hills
            .Where(h => !enemies.Any(e => e.Position.Equals(h)))
            .OrderBy(h => Body.GetDistance(h))
            .ToList();

        if (safeHills.Any())
        {
            Body.ChangeStance2(Stance.Standing);
            return safeHills.First();
        }

        var safeDitches = ditches
            .Where(d => !enemies.Any(e => e.Position.Equals(d)))
            .OrderBy(d => Body.GetDistance(d))
            .ToList();

        if (safeDitches.Any())
        {
            Body.ChangeStance2(Stance.Kneeling);
            return safeDitches.First();
        }

        var enemy = enemies.OrderBy(e => Body.GetDistance(e.Position)).First();
        return GetSavePosition(enemy.Position, enemies);
    }


    private Position GetSavePosition(Position enemy, List<EnemySnapshot> enemies)
    {
        const int MAX_ATTEMPTS = 8;
        const int MIN_SAFE_DISTANCE = 5;

        for (int distance = 3; distance <= MIN_SAFE_DISTANCE; distance++)
        {
            for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
            {

                double angle = 2 * Math.PI * attempt / MAX_ATTEMPTS;
                var safeX = Body.Position.X + (int)(Math.Cos(angle) * distance);
                var safeY = Body.Position.Y + (int)(Math.Sin(angle) * distance);

                var candidate = Position.CreatePosition(safeX, safeY);

                if (IsValidPosition(candidate, enemies))
                {
                    return candidate;
                }
            }
        }

        return FindBestAvailablePosition(enemy, enemies);
    }

    private bool IsValidPosition(Position position, List<EnemySnapshot> enemies)
    {

        bool insideBounds = position.X >= 0 && position.X < _mindLayer.Width &&
                            position.Y >= 0 && position.Y < _mindLayer.Height;

        if (!insideBounds) return false;

        bool hasBeeline = Body.HasBeeline1(position);
        if (!hasBeeline) return false;

        bool safeFromEnemies = !enemies.Any(e => GetDistance(position, e.Position) < 5);

        var barrels = Body.ExploreExplosiveBarrels1() ?? new List<Position>();
        bool safeFromBarrels = !barrels.Any(b => GetDistance(position, b) <= 3);

        var hills = Body.ExploreHills1() ?? new List<Position>();
        var ditches = Body.ExploreDitches1() ?? new List<Position>();
        bool nearCover = hills.Any(h => GetDistance(position, h) <= 2) ||
                         ditches.Any(d => GetDistance(position, d) <= 2);

        return safeFromEnemies && safeFromBarrels && (nearCover || hasBeeline);
    }

    private Position FindBestAvailablePosition(Position enemy, List<EnemySnapshot> enemies)
    {
        var hills = Body.ExploreHills1() ?? new List<Position>();
        var ditches = Body.ExploreDitches1() ?? new List<Position>();

        var nearestCover = hills.Concat(ditches)
            .Where(p => !enemies.Any(e => GetDistance(p, e.Position) < 4))
            .OrderBy(p => GetDistance(Body.Position, p))
            .FirstOrDefault();

        if (nearestCover != null)
        {
            return nearestCover;
        }

        var dx = Body.Position.X - enemy.X;
        var dy = Body.Position.Y - enemy.Y;
        var distance = Math.Max(3, Math.Min(6, GetDistance(Body.Position, enemy)));

        var newX = Body.Position.X + (Math.Sign(dx) * distance);
        var newY = Body.Position.Y + (Math.Sign(dy) * distance);

        newX = Math.Max(0, Math.Min(_mindLayer.Width - 1, newX));
        newY = Math.Max(0, Math.Min(_mindLayer.Height - 1, newY));

        return Position.CreatePosition(newX, newY);
    }

    private List<EnemySnapshot> getNearEnemies(List<EnemySnapshot> enemies)
    {
        if (enemies == null) return new List<EnemySnapshot>();
        return enemies.Where(e => Body.GetDistance(e.Position) <= 5).ToList();
    }

    private bool EnemyIsNear(List<EnemySnapshot> enemies)
    {
        if (enemies == null) return false;
        var nearbyEnemies = enemies
            .Where(e => Body.GetDistance(e.Position) <= Body.VisualRange && Body.HasBeeline1(e.Position))
            .OrderBy(e => Body.GetDistance(e.Position))
            .ThenBy(e => e.Stance == Stance.Standing)
            .ToList();

        return nearbyEnemies.Any();

    }

    private bool CanShoot()
    {
        return Body.RemainingShots > 0;
    }
}
#endregion

#region State
public class JeMaFeState : IEquatable<JeMaFeState>{
    
    public Position AgentPosition { get; }
    public bool EnemyNearby { get; }
    public bool HasLowEnergy { get; }
    public bool CanShootAgent { get; }
    public bool CarryingFlag { get; }
    public bool OnHill { get; }
    public bool OnDitch { get; }
    public bool EnemyFlagNearby { get; }
    public bool OwnBaseNearby { get; }
    public Stance CurrentStance { get; } // This will be LaserTagBox.Model.Shared.Stance
    public bool IsAttacker { get; }
    public bool IsDefender { get; }
    public bool IsCollector { get; }
    private readonly TeamJemaFe.AgentRole _agentRole;
    
 public JeMaFeState(Position agentPosition, bool enemyNearby, bool hasLowEnergy, bool canShootAgent,
            bool carryingFlag, bool onHill, bool onDitch, bool enemyFlagNearby, bool ownBaseNearby, Stance currentStance, TeamJemaFe.AgentRole role)
        {
            AgentPosition = agentPosition;
            EnemyNearby = enemyNearby;
            HasLowEnergy = hasLowEnergy;
            CanShootAgent = canShootAgent;
            CarryingFlag = carryingFlag;
            OnHill = onHill;
            OnDitch = onDitch;
            EnemyFlagNearby = enemyFlagNearby;
            OwnBaseNearby = ownBaseNearby;
            CurrentStance = currentStance;
            
            IsAttacker = role == TeamJemaFe.AgentRole.Attacker;
            IsDefender = role == TeamJemaFe.AgentRole.Defender;
            IsCollector = role == TeamJemaFe.AgentRole.Collector;
            _agentRole = role;
        }

        public TeamJemaFe.AgentRole GetAgentRole()
        {
            return _agentRole;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = AgentPosition.GetHashCode();
                hashCode = (hashCode * 397) ^ EnemyNearby.GetHashCode();
                hashCode = (hashCode * 397) ^ HasLowEnergy.GetHashCode();
                hashCode = (hashCode * 397) ^ CanShootAgent.GetHashCode();
                hashCode = (hashCode * 397) ^ CarryingFlag.GetHashCode();
                hashCode = (hashCode * 397) ^ OnHill.GetHashCode();
                hashCode = (hashCode * 397) ^ OnDitch.GetHashCode();
                hashCode = (hashCode * 397) ^ EnemyFlagNearby.GetHashCode();
                hashCode = (hashCode * 397) ^ OwnBaseNearby.GetHashCode();
                hashCode = (hashCode * 397) ^ (int)CurrentStance;
                hashCode = (hashCode * 397) ^ (int)_agentRole;
                return hashCode;
            }
        }

        public bool Equals(JeMaFeState other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return AgentPosition.Equals(other.AgentPosition) &&
                   EnemyNearby == other.EnemyNearby &&
                   HasLowEnergy == other.HasLowEnergy && CanShootAgent == other.CanShootAgent &&
                   CarryingFlag == other.CarryingFlag && OnHill == other.OnHill && OnDitch == other.OnDitch &&
                   EnemyFlagNearby == other.EnemyFlagNearby && OwnBaseNearby == other.OwnBaseNearby &&
                   CurrentStance == other.CurrentStance &&
                   _agentRole == other._agentRole;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((JeMaFeState)obj);
        }
    }

#endregion

  #region QTableEntry
  public class QTableEntry
    {
        [BsonId]
        public string Id { get; }
        public double AgentPositionX { get; } 
        public double AgentPositionY { get; }
        public bool EnemyNearby { get;}
        public bool HasLowEnergy { get;}
        public bool CanShootAgent { get;}
        public bool CarryingFlag { get; }
        public bool OnHill { get; }
        public bool OnDitch { get; set; }
        public bool EnemyFlagNearby { get; set; }
        public bool OwnBaseNearby { get; set; }
        public Stance CurrentStance { get; set; } 
        public bool IsAttacker { get; set; }
        public bool IsDefender { get; set; }
        public bool IsCollector { get; set; }
        public TeamJemaFe.AgentRole Role { get; } 
        public TeamJemaFe.PlayerAction Action { set; get; }
        public double QValue { get; set; }
        public QTableEntry() { }

        public QTableEntry(JeMaFeState state, TeamJemaFe.PlayerAction action, double qValue)
        {
            AgentPositionX = state.AgentPosition.X;
            AgentPositionY = state.AgentPosition.Y;
            EnemyNearby = state.EnemyNearby;
            HasLowEnergy = state.HasLowEnergy;
            CanShootAgent = state.CanShootAgent;
            CarryingFlag = state.CarryingFlag;
            OnHill = state.OnHill;
            OnDitch = state.OnDitch;
            EnemyFlagNearby = state.EnemyFlagNearby;
            OwnBaseNearby = state.OwnBaseNearby;
            CurrentStance = state.CurrentStance;
            IsAttacker = state.IsAttacker;
            IsDefender = state.IsDefender;
            IsCollector = state.IsCollector;
            Role = state.GetAgentRole();

            Action = action;
            QValue = qValue;
            Id = EncodeStateActionKey(state, action);
        }
        
        public JeMaFeState ToJeMaFeState()
        {
            return new JeMaFeState(
                Position.CreatePosition(AgentPositionX, AgentPositionY),
                EnemyNearby,
                HasLowEnergy,
                CanShootAgent,
                CarryingFlag,
                OnHill,
                OnDitch,
                EnemyFlagNearby,
                OwnBaseNearby,
                CurrentStance, 
                Role
            );
        }
        
        public static string EncodeStateActionKey(JeMaFeState state, TeamJemaFe.PlayerAction action)
        {
            return $"{state.AgentPosition.X},{state.AgentPosition.Y}_" +
                   $"{state.EnemyNearby}_{state.HasLowEnergy}_{state.CanShootAgent}_" +
                   $"{state.CarryingFlag}_{state.OnHill}_{state.OnDitch}_{state.EnemyFlagNearby}_" +
                   $"{state.OwnBaseNearby}_{state.CurrentStance}_" + 
                   $"{state.IsAttacker}_{state.IsDefender}_{state.IsCollector}_" +
                   $"{state.GetAgentRole()}|{action}";
        }
    }

#endregion

#region QLearning Agent

public class JeMaFeLearning
{
    private PlayerMindLayer _mindLayer;
    private Position _goal;
    private Position _initPos;
    private bool _ctfMode;
    private TeamJemaFe.AgentRole _role; 

    // Q-learning parameters
    private static readonly object _qTableLock = new object(); 
    private static readonly string _qTableDir = GetQTableDirectory();
    private static readonly string _dbPath = Path.Combine(_qTableDir, "JeMaFe_qtable.db"); // LiteDB database file
    private static readonly string _metaPath = Path.Combine(_qTableDir, "JeMaFe_meta.json"); // For epsilon (using System.Text.Json)

    private static double _epsilon = 1.0; 
    private static double _epsilonDecay = 0.99995; 
    private static double _minEpsilon = 0.1; 
    private static double _learningRate = 0.5; 
    private static double _discountFactor = 0.9; 

    private int _tickCount = 0;
    private PlayerBody _body;
    
    public void Init(PlayerMindLayer mindLayer, PlayerBody body)
    {
        _body = body;
        _mindLayer = mindLayer;
        _initPos = _body.Position.Copy();
        _ctfMode = _body.ExploreOwnFlagStand() != null;

        Directory.CreateDirectory(_qTableDir); // Ensure the QTables directory exists
        
        // Initialize LiteDB and ensure the index is present for performance
        using (var db = new LiteDatabase(_dbPath))
        {
            var col = db.GetCollection<QTableEntry>("q_table");
            col.EnsureIndex(x => x.Id, true); // Create a unique index on Id for fast lookups
        }

        LoadOrInitializeQTable();
    }

    public void Tick()
    {
        _tickCount++;

        var currentState = GetCurrentState();
        TeamJemaFe.PlayerAction chosenAction;
        
        // Lock ensures thread safety when accessing shared resources (like LiteDB/epsilon)
        lock (_qTableLock) 
        {
            chosenAction = ChooseAction(currentState);
        }
        
        PerformAction(chosenAction);
        var newState = GetCurrentState();
        var reward = CalculateReward(currentState, chosenAction, newState);
        
        lock (_qTableLock) 
        {
            UpdateQValue(currentState, chosenAction, reward, newState);
        }
        
        DecreaseEpsilon();
        // Save epsilon frequently, Q-table updates are handled by LiteDB internally.
        if (_tickCount % 100 == 0) 
        {
            SaveQTable();
        }
    }

    /// <summary>
    /// Defines the current state of the agent.
    /// </summary>
    private JeMaFeState GetCurrentState()
    {
        var enemies = _body.ExploreEnemies1() ?? new List<EnemySnapshot>();
        var hills = _body.ExploreHills1() ?? new List<Position>();
        var ditches = _body.ExploreDitches1() ?? new List<Position>();
        var enemyFlags = _body.ExploreEnemyFlagStands1() ?? new List<Position>();
        var ownBase = _body.ExploreOwnFlagStand();

        bool enemyNearby = EnemyIsNear(enemies);
        bool hasLowEnergy = _body.Energy < 35;
        bool canShootAgent = CanShoot();
        bool carryingFlag = _body.CarryingFlag;
        bool onHill = hills.Any(h => h.Equals(_body.Position));
        bool onDitch = ditches.Any(d => d.Equals(_body.Position));
        bool enemyFlagNearby = enemyFlags.Any(f => _body.GetDistance(f) < 5); 
        bool ownBaseNearby = ownBase != null && _body.GetDistance(ownBase) < 5;

        return new JeMaFeState(_body.Position, enemyNearby, hasLowEnergy, canShootAgent,
            carryingFlag, onHill, onDitch, enemyFlagNearby, ownBaseNearby, _body.Stance, _role);
    }

    /// <summary>
    /// Chooses an action based on the current state using an epsilon-greedy policy.
    /// </summary>
    private TeamJemaFe.PlayerAction ChooseAction(JeMaFeState state)
    {
        var possibleActions = GetPossibleActions(state);
        if (!possibleActions.Any())
        {
            return TeamJemaFe.PlayerAction.MoveRandom; // Fallback
        }

        if (RandomHelper.Random.NextDouble() < _epsilon)
        {
            // Explore: Choose a random action
            return possibleActions[RandomHelper.Random.Next(possibleActions.Count)];
        }
        else
        {
            // Exploit: Choose the action with the highest Q-value
            return possibleActions.OrderByDescending(action => GetQValue(state, action)).First();
        }
    }

    /// <summary>
    /// Returns a list of possible actions the agent can take from the current state.
    /// </summary>
    private List<TeamJemaFe.PlayerAction> GetPossibleActions(JeMaFeState state)
    {
        var actions = new List<TeamJemaFe.PlayerAction>();

        // Movement actions
        actions.Add(TeamJemaFe.PlayerAction.MoveRandom);
        
        // Combat actions
        if (state.CanShootAgent)
        {
            actions.Add(TeamJemaFe.PlayerAction.AttackEnemy);
            actions.Add(TeamJemaFe.PlayerAction.ExplodeBarrel);
        }
        else
        {
            actions.Add(TeamJemaFe.PlayerAction.Reload);
        }

        if (state.EnemyNearby && state.HasLowEnergy)
        {
            actions.Add(TeamJemaFe.PlayerAction.EvadeEnemy);
        }

        // Stance changes
        actions.Add(TeamJemaFe.PlayerAction.ChangeStanceLying);
        actions.Add(TeamJemaFe.PlayerAction.ChangeStanceKneeling);
        actions.Add(TeamJemaFe.PlayerAction.ChangeStanceStanding);

        // CTF specific actions
        if (_ctfMode)
        {
            if (state.CarryingFlag)
            {
                actions.Add(TeamJemaFe.PlayerAction.MoveToOwnBase);
            }
            else
            {
                actions.Add(TeamJemaFe.PlayerAction.MoveToEnemyFlag);
            }
        }
        else // Deathmatch specific
        {
            actions.Add(TeamJemaFe.PlayerAction.MoveToHill);
            actions.Add(TeamJemaFe.PlayerAction.MoveToDitch);
        }
        
        return actions.Distinct().ToList();
    }

    /// <summary>
    /// Performs the chosen action on the agent's body.
    /// </summary>
    private void PerformAction(TeamJemaFe.PlayerAction action)
    {
        var enemies = _body.ExploreEnemies1() ?? new List<EnemySnapshot>();
        var ditches = _body.ExploreDitches1() ?? new List<Position>();
        var hills = _body.ExploreHills1() ?? new List<Position>();
        var barrels = _body.ExploreExplosiveBarrels1() ?? new List<Position>();
        var enemyFlags = _body.ExploreEnemyFlagStands1() ?? new List<Position>();
        var ownBase = _body.ExploreOwnFlagStand();

        switch (action)
        {
            case TeamJemaFe.PlayerAction.MoveRandom:
                _goal = GetRandomPosition();
                MoveToGoal();
                break;
            case TeamJemaFe.PlayerAction.MoveToEnemy:
                if (enemies.Any())
                {
                    var nearestEnemy = enemies.OrderBy(e => _body.GetDistance(e.Position)).First();
                    _goal = nearestEnemy.Position;
                    MoveToGoal();
                }
                else
                {
                    _goal = GetRandomPosition(); // Fallback if no enemy
                    MoveToGoal();
                }
                break;
            case TeamJemaFe.PlayerAction.MoveToHill:
                if (hills.Any())
                {
                    _goal = hills.OrderBy(h => _body.GetDistance(h)).First();
                    MoveToGoal();
                }
                break;
            case TeamJemaFe.PlayerAction.MoveToDitch:
                if (ditches.Any())
                {
                    _goal = ditches.OrderBy(d => _body.GetDistance(d)).First();
                    MoveToGoal();
                }
                break;
            case TeamJemaFe.PlayerAction.MoveToOwnBase:
                if (ownBase != null)
                {
                    _goal = ownBase;
                    MoveToGoal();
                }
                break;
            case TeamJemaFe.PlayerAction.MoveToEnemyFlag:
                if (enemyFlags.Any())
                {
                    _goal = enemyFlags.OrderBy(f => _body.GetDistance(f)).First();
                    MoveToGoal();
                }
                break;
            case TeamJemaFe.PlayerAction.AttackEnemy:
                if (enemies.Any() && CanShoot())
                {
                    var target = enemies.OrderBy(e => _body.GetDistance(e.Position)).First();
                    _body.Tag5(target.Position);
                }
                break;
            case TeamJemaFe.PlayerAction.Reload:
                _body.Reload3();
                break;
            case TeamJemaFe.PlayerAction.ChangeStanceLying:
                _body.ChangeStance2(Stance.Lying); 
                break;
            case TeamJemaFe.PlayerAction.ChangeStanceKneeling:
                _body.ChangeStance2(Stance.Kneeling);
                break;
            case TeamJemaFe.PlayerAction.ChangeStanceStanding:
                _body.ChangeStance2(Stance.Standing);
                break;
            case TeamJemaFe.PlayerAction.EvadeEnemy:
                if (enemies.Any())
                {
                    RunAwayFromEnemy(enemies);
                }
                break;
            case TeamJemaFe.PlayerAction.ExplodeBarrel:
                TryExplodeBarrel(enemies, barrels);
                break;
        }
    }

    /// <summary>
    /// Calculates the reward for performing an action.
    /// </summary>
    private double CalculateReward(JeMaFeState oldState, TeamJemaFe.PlayerAction action, JeMaFeState newState)
    {
        double reward = 0;

        reward -= 0.1; // Small penalty for each action

        if (action == TeamJemaFe.PlayerAction.AttackEnemy && oldState.EnemyNearby && oldState.CanShootAgent)
        {
            if (_role == TeamJemaFe.AgentRole.Attacker)
            {
                reward += 50.0;
            }
            reward += 5.0; // General reward for attacking if applicable
        }

        // CTF specific rewards
        if (_ctfMode && !oldState.CarryingFlag && newState.CarryingFlag)
        {
            reward += 100.0; // Picking up flag
        }
        if (_ctfMode && oldState.CarryingFlag && !newState.CarryingFlag && newState.OwnBaseNearby)
        {
            reward += 200.0; // Scoring flag
        }

        // Penalty for being vulnerable
        if (newState.EnemyNearby && newState.HasLowEnergy)
        {
            reward -= 50.0;
        }

        // Reward for successful evasion
        if (oldState.HasLowEnergy && oldState.EnemyNearby && action == TeamJemaFe.PlayerAction.EvadeEnemy)
        {
            if (!newState.EnemyNearby || newState.OnHill || newState.OnDitch)
            {
                reward += 30.0;
            }
        }
        
        // Reward for being in cover when enemy is near
        if (newState.EnemyNearby && (newState.OnHill || newState.OnDitch))
        {
            reward += 10.0;
        }

        // Penalty for being too close to explosive barrels (if not intending to use them)
        var barrels = _body.ExploreExplosiveBarrels1() ?? new List<Position>();
        if (barrels.Any(b => _body.GetDistance(b) <= 2) || (newState.EnemyNearby && !oldState.EnemyNearby))
        {
            reward -= 20.0;
        }

        // Reward for reloading if needed and safe
        if (action == TeamJemaFe.PlayerAction.Reload && !oldState.CanShootAgent && !oldState.EnemyNearby)
        {
            reward += 2.0;
        }

        // Reward for exploding the barrel effectively
        var enemiesAfterAction = _body.ExploreEnemies1() ?? new List<EnemySnapshot>();
        if (action == TeamJemaFe.PlayerAction.ExplodeBarrel && enemiesAfterAction.Count < (_body.ExploreEnemies1() ?? new List<EnemySnapshot>()).Count) 
        {
            reward += 75.0;
        }

        return reward;
    }

    /// <summary>
    /// Retrieves the Q-value for a given state-action pair from LiteDB. Initializes to 0 if not seen before.
    /// </summary>
    private double GetQValue(JeMaFeState state, TeamJemaFe.PlayerAction action)
    {
        // This method is called within a lock.
        using (var db = new LiteDatabase(_dbPath))
        {
            var col = db.GetCollection<QTableEntry>("q_table");
            var id = QTableEntry.EncodeStateActionKey(state, action);
            var entry = col.FindById(id);
            return entry?.QValue ?? 0.0; // Returns existing QValue or 0.0 if not found
        }
    }

    /// <summary>
    /// Updates the Q-value for a state-action pair using the Q-learning formula and stores it in LiteDB.
    /// </summary>
    private void UpdateQValue(JeMaFeState state, TeamJemaFe.PlayerAction action, double reward, JeMaFeState newState)
    {
        // This method is called within a lock.
        using (var db = new LiteDatabase(_dbPath))
        {
            var col = db.GetCollection<QTableEntry>("q_table");
            var id = QTableEntry.EncodeStateActionKey(state, action);
            
            // Get current Q-value
            double oldQ = GetQValue(state, action); 

            // Get the maximum Q-value for the new state over all possible actions
            double maxFutureQ = GetPossibleActions(newState)
                .Select(nextAction => GetQValue(newState, nextAction)) // Recursively calls GetQValue
                .DefaultIfEmpty(0.0) // If no actions, max future Q is 0
                .Max();

            // Q-learning formula
            double newQ = oldQ + _learningRate * (reward + _discountFactor * maxFutureQ - oldQ);

            // Create or update the QTableEntry in the database
            var entry = new QTableEntry(state, action, newQ);
            col.Upsert(entry); // Upsert will insert if Id doesn't exist, update if it does.
        }
    }

    /// <summary>
    /// Decreases the exploration rate (epsilon) over time.
    /// </summary>
    private void DecreaseEpsilon()
    {
        lock (_qTableLock) 
        {
            if (_tickCount < 5000) 
                _epsilon = Math.Max(_minEpsilon, _epsilon * _epsilonDecay);
            else 
                _epsilon = Math.Max(_minEpsilon, _epsilon * 0.99999); // Slower decay after initial exploration
        }
    }

    /// <summary>
    /// Saves the current epsilon to a JSON file. The Q-table is managed by LiteDB.
    /// </summary>
    public static void SaveQTable() 
    {
        lock (_qTableLock)
        {
            var meta = new Dictionary<string, double> { { "epsilon", _epsilon } };
            // Use the SystemTextJson alias for System.Text.Json.JsonSerializer
            File.WriteAllText(_metaPath, SystemTextJson.JsonSerializer.Serialize(meta, new SystemTextJson.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Loads the epsilon value from the meta JSON file, or initializes if not found.
    /// </summary>
    private static void LoadOrInitializeQTable()
    {
        if (File.Exists(_metaPath))
        {
            try
            {
                var meta = SystemTextJson.JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_metaPath));
                
                if (meta != null && meta.ContainsKey("epsilon")) 
                    _epsilon = meta["epsilon"];
                Console.WriteLine("[JeMaFeLearning] Epsilon loaded successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JeMaFeLearning] Error loading Epsilon: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[JeMaFeLearning] No existing meta file found. Initializing epsilon.");
        }

        Console.WriteLine($"[JeMaFeLearning] Q-Table will be managed by LiteDB at: {_dbPath}");
    }

    /// <summary>
    /// Provides the directory for Q-tables, ensuring it's relative to the project root.
    /// </summary>
    private static string GetQTableDirectory()
    {
        string basePath = AppDomain.CurrentDomain.BaseDirectory; 
        string projectRoot = Path.Combine(basePath, "..", "..", "..");
        projectRoot = Path.GetFullPath(projectRoot);

        string resourceRoot = Path.Combine(projectRoot,"Resources");
        
        string qTablesFolder = Path.Combine(resourceRoot, "QTables");
        
        Directory.CreateDirectory(qTablesFolder); 

        return qTablesFolder;
    }
    
    #endregion

    #region Helper Methods

    private Position GetRandomPosition()
    {
        var barrels = _body.ExploreExplosiveBarrels1() ?? new List<Position>();
        for (int i = 0; i < 5; i++)
        {
            var x = RandomHelper.Random.Next(_mindLayer.Width);
            var y = RandomHelper.Random.Next(_mindLayer.Height);
            var pos = Position.CreatePosition(x, y);

            bool tooCloseToBarrel = barrels.Any(b => _body.GetDistance(pos) <= 3);

            if (_body.HasBeeline1(pos) && !tooCloseToBarrel)
                return pos;
        }
        return _body.Position;
    }
    
    private Position GetSavePosition(Position enemy, List<EnemySnapshot> enemies)
    {
        const int MAX_ATTEMPTS = 8;  
        const int MIN_SAFE_DISTANCE = 5;  
        
        for (int distance = 3; distance <= MIN_SAFE_DISTANCE; distance++)
        {
            for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
            {
                double angle = 2 * Math.PI * attempt / MAX_ATTEMPTS;
                var safeX = _body.Position.X + (int)(Math.Cos(angle) * distance);
                var safeY = _body.Position.Y + (int)(Math.Sin(angle) * distance);
                
                var candidate = Position.CreatePosition(safeX, safeY);

                if (IsValidPosition(candidate, enemies))
                {
                    return candidate;
                }
            }
        }
        return FindBestAvailablePosition(enemy, enemies);
    }

    private bool IsValidPosition(Position position, List<EnemySnapshot> enemies)
    {
        bool insideBounds = position.X >= 0 && position.X < _mindLayer.Width &&
                           position.Y >= 0 && position.Y < _mindLayer.Height;
        
        if (!insideBounds) return false;
        
        bool hasBeeline = _body.HasBeeline1(position);
        if (!hasBeeline) return false;
        
        bool safeFromEnemies = !enemies.Any(e => GetDistance(position, e.Position) < 5);
        
        var barrels = _body.ExploreExplosiveBarrels1() ?? new List<Position>();
        bool safeFromBarrels = !barrels.Any(b => GetDistance(position, b) <= 3);
        
        var hills = _body.ExploreHills1() ?? new List<Position>();
        var ditches = _body.ExploreDitches1() ?? new List<Position>();
        bool nearCover = hills.Any(h => GetDistance(position, h) <= 2) || 
                        ditches.Any(d => GetDistance(position, d) <= 2);

        return safeFromEnemies && safeFromBarrels && (nearCover || hasBeeline);
    }

    private Position FindBestAvailablePosition(Position enemy, List<EnemySnapshot> enemies)
    {
        var hills = _body.ExploreHills1() ?? new List<Position>();
        var ditches = _body.ExploreDitches1() ?? new List<Position>();
        
        var nearestCover = hills.Concat(ditches)
            .Where(p => !enemies.Any(e => GetDistance(p, e.Position) < 4))
            .OrderBy(p => GetDistance(_body.Position, p))
            .FirstOrDefault();

        if (nearestCover != null)
        {
            return nearestCover;
        }
        
        var dx = _body.Position.X - enemy.X;
        var dy = _body.Position.Y - enemy.Y;
        
        double escapeAngle = Math.Atan2(dy, dx); 
        double escapeDistance = Math.Max(3, Math.Min(6, GetDistance(_body.Position, enemy)));

        var newX = _body.Position.X + (int)(Math.Cos(escapeAngle) * escapeDistance);
        var newY = _body.Position.Y + (int)(Math.Sin(escapeAngle) * escapeDistance);

        newX = Math.Max(0, Math.Min(_mindLayer.Width - 1, newX));
        newY = Math.Max(0, Math.Min(_mindLayer.Height - 1, newY));
        
        return Position.CreatePosition(newX, newY);
    }

    private int GetDistance(Position a, Position b)
    {
        return (int)(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y));
    }

    private bool TryExplodeBarrel(List<EnemySnapshot> enemies, List<Position> barrels)
    {
        foreach (var barrel in barrels)
        {
            if (enemies.Any(e => _body.GetDistance(barrel) <= 3))
            {
                _body.ChangeStance2(Stance.Lying); 
                _body.Tag5(barrel);
                return true;
            }
        }
        return false;
    }

    private void RunAwayFromEnemy(List<EnemySnapshot> enemies)
    {
        var enemy = enemies.OrderBy(e => _body.GetDistance(e.Position)).First();
        _goal = GetSavePosition(enemy.Position, enemies);
        _body.GoTo(_goal);
    }

    private void MoveToGoal()
    {
        if (_goal == null) return;

        var moved = _body.GoTo(_goal);
        if (!moved)
        {
            _goal = null; 
        }
    }

    private bool EnemyIsNear(List<EnemySnapshot> enemies)
    {
        return enemies.Any(e => _body.GetDistance(e.Position) <= 5);
    }

    private bool CanShoot()
    {
        return _body.RemainingShots > 0;
    }
    
    public void SetRole(TeamJemaFe.AgentRole roleName)
    {
       _role = roleName;
    }

    #endregion
}


