using System;
using System.Collections.Generic;
using System.Linq;
using LaserTagBox.Model.Body;
using LaserTagBox.Model.Shared;
using Mars.Common.Core.Random;
using Mars.Interfaces.Environments;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LaserTagBox.Model.Mind;

public class TeamJemaFe : AbstractPlayerMind
{
    private JeMaFeLearning qlearning;
    private JeMaFeLearning qlearning2;
    private JeMaFeLearning qlearning3;
    private PlayerMindLayer _mindLayer;
    private Position _goal;
    private Position InitPo;
    private AgentRole _role;
    private Color _ourColor;
    
    private static int globalAgentCounter = 0;
    private int myAgentIndex;
    
    public override void Init(PlayerMindLayer mindLayer)
    {
        _mindLayer = mindLayer;
        InitPo = Body.Position.Copy();
        
        myAgentIndex = globalAgentCounter++; 

        if (myAgentIndex % 3  == 0)
        {
            _role = AgentRole.Collector;
            qlearning = new JeMaFeLearning(); 
            qlearning.Init(mindLayer,(PlayerBody)this.Body);
            qlearning.SetRole(_role);
            
        }
        else 
        {
            switch (myAgentIndex)
            {
                case var n when n % 3 == 1: _role = AgentRole.Collector; break;
                case var n when n % 3 == 2: _role = AgentRole.Collector; break;
                default: _role = AgentRole.Defender; break;
            }
        }

        Console.WriteLine($"Agent {myAgentIndex}: {(qlearning != null ? "Q-Learning" : "RuleBased")} Role: {_role}");
    }

    public override void Tick()
    {
        if (qlearning != null)
        {
            qlearning.Tick();
            return;
        }
        
        if (qlearning2 != null)
        {
            qlearning2.Tick();
            return;
        }
        
        if (qlearning3 != null)
        {
            qlearning3.Tick();
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
        var ourFlag = flags.FirstOrDefault(flag => GetDistance(flag.Position,ownBase) == 0);
        if (!ourFlag.Equals(default(FlagSnapshot)))
        {
            if (ourFlag.Team == Color.Blue)
            {
                _ourColor = Color.Blue;
            }else if (ourFlag.Team == Color.Red)
            {
                _ourColor = Color.Red;
            }else if (ourFlag.Team == Color.Yellow)
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
                    AttackerBehavior(ownBase, enemyFlags, enemies, ditches,hills,barrels);
                    break;
                case AgentRole.Defender:
                    DefenderBehavior( ownBase, enemyFlags, enemies, ditches,hills,barrels);
                    break;
                case AgentRole.Collector: 
                    FlagCarrierBehavior(ownBase, enemyFlags, enemies, ditches,hills,barrels);
                    break;
            }
        }
    
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
    var teamNearBy = Body.ExploreTeam().Where(t => GetDistance(t.Position, Body.Position) <= Body.VisualRange).FirstOrDefault();
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
    
        if (!teamHasFlag && !iHaveFlag )
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
                if (Body.GetDistance(ownBase) <=1)
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
            if (enemies.Any(e => GetDistance(barrel,e.Position) <= 2))
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
            .Where(e => Body.HasBeeline1(e.Position) && Body.GetDistance(e.Position) <= Body.VisualRange).
            OrderBy(e => e.Stance != Stance.Standing).ThenBy(e => Body.GetDistance(e.Position)).ToList();

        if (!targets.Any())
        {
            Console.WriteLine("No targets found!");
            return;
        }

        if (targets.Any())
        {
            var target = targets.First();
            var distance = Body.GetDistance(target.Position);

            if (distance <= 4)
            {
                Body.ChangeStance2(Stance.Lying);
            }else if (distance <=7)
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
        
        if (Body.GetDistance(enemyPos) < Body.GetDistance(Position.CreatePosition(Body.Position.X + deltaX, Body.Position.Y + deltaY)))
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

    var newX = Body.Position.X + (int)(Math.Sign(dx) * distance);
    var newY = Body.Position.Y + (int)(Math.Sign(dy) * distance);
    
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