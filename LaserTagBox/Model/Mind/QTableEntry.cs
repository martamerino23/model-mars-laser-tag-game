// File: QTableEntry.cs
using LiteDB; // Crucial for [BsonId]
using Mars.Interfaces.Environments; // For Position

using LaserTagBox.Model.Mind;    // For AgentRole, PlayerAction
using LaserTagBox.Model.Shared;  // For Stance - CONFIRMED TO BE LaserTagBox.Model.Shared.Stance

namespace LaserTagBox.Model.Mind
{
    // This class represents a single entry in your Q-table, stored as a document in LiteDB.
    public class QTableEntry
    {
        [BsonId] // LiteDB uses this property as the unique identifier for the document
        public string Id { get; set; }

        // State properties - must be public for LiteDB to access them
        public double AgentPositionX { get; set; } // Store Position components separately
        public double AgentPositionY { get; set; }
        public bool EnemyNearby { get; set; }
        public bool HasLowEnergy { get; set; }
        public bool CanShootAgent { get; set; }
        public bool CarryingFlag { get; set; }
        public bool OnHill { get; set; }
        public bool OnDitch { get; set; }
        public bool EnemyFlagNearby { get; set; }
        public bool OwnBaseNearby { get; set; }
        public Stance CurrentStance { get; set; } // <--- Confirmed: This will be LaserTagBox.Model.Shared.Stance
        
        // Agent Role and derived state properties
        public bool IsAttacker { get; set; }
        public bool IsDefender { get; set; }
        public bool IsCollector { get; set; }
        public AgentRole Role { get; set; } // Assuming AgentRole is in LaserTagBox.Model.Mind

        // Action and Q-Value
        public PlayerAction Action { get; set; }
        public double QValue { get; set; }

        public QTableEntry() { } // Parameterless constructor is needed for LiteDB deserialization

        public QTableEntry(JeMaFeState state, PlayerAction action, double qValue)
        {
            // Map JeMaFeState properties to QTableEntry properties
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
            Role = state.GetAgentRole(); // GetAgentRole() ensures we use the internal _agentRole

            Action = action;
            QValue = qValue;

            // The ID should uniquely identify a (state, action) pair
            Id = EncodeStateActionKey(state, action);
        }

        // Helper to convert QTableEntry back to JeMaFeState for internal use
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
                CurrentStance, // <--- Correctly uses LaserTagBox.Model.Shared.Stance
                Role
            );
        }

        // Static method to generate the unique ID string for a (state, action) pair
        // This MUST be consistent for saving and loading.
        public static string EncodeStateActionKey(JeMaFeState state, PlayerAction action)
        {
            // This key encodes all relevant state properties and the action
            // It MUST match the decoding logic perfectly.
            return $"{state.AgentPosition.X},{state.AgentPosition.Y}_" +
                   $"{state.EnemyNearby}_{state.HasLowEnergy}_{state.CanShootAgent}_" +
                   $"{state.CarryingFlag}_{state.OnHill}_{state.OnDitch}_{state.EnemyFlagNearby}_" +
                   $"{state.OwnBaseNearby}_{state.CurrentStance}_" + 
                   $"{state.IsAttacker}_{state.IsDefender}_{state.IsCollector}_" +
                   $"{state.GetAgentRole()}|{action}";
        }
    }
}