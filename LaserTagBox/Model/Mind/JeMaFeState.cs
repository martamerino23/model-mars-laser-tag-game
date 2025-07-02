// File: JeMaFeState.cs
using System;
using Mars.Interfaces.Environments; // For Position
using LaserTagBox.Model.Mind;      // For AgentRole
using LaserTagBox.Model.Shared;    // For Stance - ensure this is present

namespace LaserTagBox.Model.Mind
{
    public class JeMaFeState : IEquatable<JeMaFeState>
    {
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
        private readonly AgentRole _agentRole;

        public JeMaFeState(Position agentPosition, bool enemyNearby, bool hasLowEnergy, bool canShootAgent,
            bool carryingFlag, bool onHill, bool onDitch, bool enemyFlagNearby, bool ownBaseNearby, Stance currentStance, AgentRole role)
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
            
            IsAttacker = role == AgentRole.Attacker;
            IsDefender = role == AgentRole.Defender;
            IsCollector = role == AgentRole.Collector;
            _agentRole = role;
        }

        public AgentRole GetAgentRole()
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
}