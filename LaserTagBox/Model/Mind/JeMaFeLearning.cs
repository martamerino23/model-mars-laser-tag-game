// File: JeMaFeLearning.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SystemTextJson = System.Text.Json; // <--- ALIAS for System.Text.Json to resolve JsonSerializer ambiguity
using LaserTagBox.Model.Body;
using LaserTagBox.Model.Shared; // For Stance enum
using Mars.Common.Core.Random;
using Mars.Interfaces.Environments;
using LiteDB; // <--- LiteDB for Q-table persistence

namespace LaserTagBox.Model.Mind;

public class JeMaFeLearning
{
    private PlayerMindLayer _mindLayer;
    private Position _goal;
    private Position _initPos;
    private bool _ctfMode;
    private AgentRole _role; 

    // Q-learning parameters
    private static readonly object qTableLock = new object(); 
    private static readonly string qTableDir = GetQTableDirectory();
    private static readonly string dbPath = Path.Combine(qTableDir, "JeMaFe_qtable.db"); // LiteDB database file
    private static readonly string metaPath = Path.Combine(qTableDir, "JeMaFe_meta.json"); // For epsilon (using System.Text.Json)

    private static double epsilon = 1.0; 
    private static double epsilonDecay = 0.99995; 
    private static double minEpsilon = 0.1; 
    private static double learningRate = 0.5; 
    private static double discountFactor = 0.9; 

    private int _tickCount = 0;
    private PlayerBody _body;
    

    public void Init(PlayerMindLayer mindLayer, PlayerBody body)
    {
        _body = body;
        _mindLayer = mindLayer;
        _initPos = _body.Position.Copy();
        _ctfMode = _body.ExploreOwnFlagStand() != null;

        Directory.CreateDirectory(qTableDir); // Ensure the QTables directory exists
        
        // Initialize LiteDB and ensure the index is present for performance
        using (var db = new LiteDatabase(dbPath))
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
        PlayerAction chosenAction;
        
        // Lock ensures thread safety when accessing shared resources (like LiteDB/epsilon)
        lock (qTableLock) 
        {
            chosenAction = ChooseAction(currentState);
        }
        
        PerformAction(chosenAction);
        var newState = GetCurrentState();
        var reward = CalculateReward(currentState, chosenAction, newState);
        
        lock (qTableLock) 
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
    private PlayerAction ChooseAction(JeMaFeState state)
    {
        var possibleActions = GetPossibleActions(state);
        if (!possibleActions.Any())
        {
            return PlayerAction.MoveRandom; // Fallback
        }

        if (RandomHelper.Random.NextDouble() < epsilon)
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
    private List<PlayerAction> GetPossibleActions(JeMaFeState state)
    {
        var actions = new List<PlayerAction>();

        // Movement actions
        actions.Add(PlayerAction.MoveRandom);
        
        // Combat actions
        if (state.CanShootAgent)
        {
            actions.Add(PlayerAction.AttackEnemy);
            actions.Add(PlayerAction.ExplodeBarrel);
        }
        else
        {
            actions.Add(PlayerAction.Reload);
        }

        if (state.EnemyNearby && state.HasLowEnergy)
        {
            actions.Add(PlayerAction.EvadeEnemy);
        }

        // Stance changes
        actions.Add(PlayerAction.ChangeStanceLying);
        actions.Add(PlayerAction.ChangeStanceKneeling);
        actions.Add(PlayerAction.ChangeStanceStanding);

        // CTF specific actions
        if (_ctfMode)
        {
            if (state.CarryingFlag)
            {
                actions.Add(PlayerAction.MoveToOwnBase);
            }
            else
            {
                actions.Add(PlayerAction.MoveToEnemyFlag);
            }
        }
        else // Deathmatch specific
        {
            actions.Add(PlayerAction.MoveToHill);
            actions.Add(PlayerAction.MoveToDitch);
        }
        
        return actions.Distinct().ToList();
    }

    /// <summary>
    /// Performs the chosen action on the agent's body.
    /// </summary>
    private void PerformAction(PlayerAction action)
    {
        var enemies = _body.ExploreEnemies1() ?? new List<EnemySnapshot>();
        var ditches = _body.ExploreDitches1() ?? new List<Position>();
        var hills = _body.ExploreHills1() ?? new List<Position>();
        var barrels = _body.ExploreExplosiveBarrels1() ?? new List<Position>();
        var enemyFlags = _body.ExploreEnemyFlagStands1() ?? new List<Position>();
        var ownBase = _body.ExploreOwnFlagStand();

        switch (action)
        {
            case PlayerAction.MoveRandom:
                _goal = GetRandomPosition();
                MoveToGoal();
                break;
            case PlayerAction.MoveToEnemy:
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
            case PlayerAction.MoveToHill:
                if (hills.Any())
                {
                    _goal = hills.OrderBy(h => _body.GetDistance(h)).First();
                    MoveToGoal();
                }
                break;
            case PlayerAction.MoveToDitch:
                if (ditches.Any())
                {
                    _goal = ditches.OrderBy(d => _body.GetDistance(d)).First();
                    MoveToGoal();
                }
                break;
            case PlayerAction.MoveToOwnBase:
                if (ownBase != null)
                {
                    _goal = ownBase;
                    MoveToGoal();
                }
                break;
            case PlayerAction.MoveToEnemyFlag:
                if (enemyFlags.Any())
                {
                    _goal = enemyFlags.OrderBy(f => _body.GetDistance(f)).First();
                    MoveToGoal();
                }
                break;
            case PlayerAction.AttackEnemy:
                if (enemies.Any() && CanShoot())
                {
                    var target = enemies.OrderBy(e => _body.GetDistance(e.Position)).First();
                    _body.Tag5(target.Position);
                }
                break;
            case PlayerAction.Reload:
                _body.Reload3();
                break;
            case PlayerAction.ChangeStanceLying:
                _body.ChangeStance2(Stance.Lying); 
                break;
            case PlayerAction.ChangeStanceKneeling:
                _body.ChangeStance2(Stance.Kneeling);
                break;
            case PlayerAction.ChangeStanceStanding:
                _body.ChangeStance2(Stance.Standing);
                break;
            case PlayerAction.EvadeEnemy:
                if (enemies.Any())
                {
                    RunAwayFromEnemy(enemies);
                }
                break;
            case PlayerAction.ExplodeBarrel:
                TryExplodeBarrel(enemies, barrels);
                break;
        }
    }

    /// <summary>
    /// Calculates the reward for performing an action.
    /// </summary>
    private double CalculateReward(JeMaFeState oldState, PlayerAction action, JeMaFeState newState)
    {
        double reward = 0;

        reward -= 0.1; // Small penalty for each action

        if (action == PlayerAction.AttackEnemy && oldState.EnemyNearby && oldState.CanShootAgent)
        {
            if (_role == AgentRole.Attacker)
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
        if (oldState.HasLowEnergy && oldState.EnemyNearby && action == PlayerAction.EvadeEnemy)
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
        if (action == PlayerAction.Reload && !oldState.CanShootAgent && !oldState.EnemyNearby)
        {
            reward += 2.0;
        }

        // Reward for exploding barrel effectively
        var enemiesAfterAction = _body.ExploreEnemies1() ?? new List<EnemySnapshot>();
        if (action == PlayerAction.ExplodeBarrel && enemiesAfterAction.Count < (_body.ExploreEnemies1() ?? new List<EnemySnapshot>()).Count) 
        {
            reward += 75.0;
        }

        return reward;
    }

    /// <summary>
    /// Retrieves the Q-value for a given state-action pair from LiteDB. Initializes to 0 if not seen before.
    /// </summary>
    private double GetQValue(JeMaFeState state, PlayerAction action)
    {
        // This method is called within a lock.
        using (var db = new LiteDatabase(dbPath))
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
    private void UpdateQValue(JeMaFeState state, PlayerAction action, double reward, JeMaFeState newState)
    {
        // This method is called within a lock.
        using (var db = new LiteDatabase(dbPath))
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
            double newQ = oldQ + learningRate * (reward + discountFactor * maxFutureQ - oldQ);

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
        lock (qTableLock) 
        {
            if (_tickCount < 5000) 
                epsilon = Math.Max(minEpsilon, epsilon * epsilonDecay);
            else 
                epsilon = Math.Max(minEpsilon, epsilon * 0.99999); // Slower decay after initial exploration
        }
    }

    /// <summary>
    /// Saves the current epsilon to a JSON file. The Q-table is managed by LiteDB.
    /// </summary>
    public static void SaveQTable() 
    {
        lock (qTableLock)
        {
            var meta = new Dictionary<string, double> { { "epsilon", epsilon } };
            // Use the SystemTextJson alias for System.Text.Json.JsonSerializer
            File.WriteAllText(metaPath, SystemTextJson.JsonSerializer.Serialize(meta, new SystemTextJson.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Loads the epsilon value from the meta JSON file, or initializes if not found.
    /// </summary>
    private static void LoadOrInitializeQTable()
    {
        if (File.Exists(metaPath))
        {
            try
            {
                // Use the SystemTextJson alias for System.Text.Json.JsonSerializer.Deserialize<T>()
                var meta = SystemTextJson.JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(metaPath));
                
                if (meta != null && meta.ContainsKey("epsilon")) 
                    epsilon = meta["epsilon"];
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

        Console.WriteLine($"[JeMaFeLearning] Q-Table will be managed by LiteDB at: {dbPath}");
    }

    /// <summary>
    /// Provides the directory for Q-tables, ensuring it's relative to the project root.
    /// </summary>
    private static string GetQTableDirectory()
    {
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        // Navigate up to the project root assuming standard build output path (bin/Debug/netX.Y/)
        string projectRoot = Path.Combine(basePath, "..", "..", "..");
        projectRoot = Path.GetFullPath(projectRoot);
        
        string qTablesFolder = Path.Combine(projectRoot, "QTables");
        
        Directory.CreateDirectory(qTablesFolder); // Create directory if it doesn't exist

        return qTablesFolder;
    }

    #region Helper Methods (Unchanged from previous versions unless specifically noted)

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
    
    public void SetRole(AgentRole roleName)
    {
       _role = roleName;
    }

    #endregion
}