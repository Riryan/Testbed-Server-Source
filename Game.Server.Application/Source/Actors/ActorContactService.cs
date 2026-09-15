using System;
using Game.Shared.Actors;

namespace Game.Server.Application.Actors
{
    public enum ActorContactSeverity : byte
    {
        None = 0,
        Brush = 1,
        Bump = 2,
        HeavyImpact = 3,
        Knockdown = 4,
    }

    public readonly struct ActorContactEvent
    {
        public AuthoritativeActorHandle ActorA { get; }
        public AuthoritativeActorHandle ActorB { get; }
        public ActorContactSeverity Severity { get; }
        public float DirectionX { get; }
        public float DirectionZ { get; }
        public float RelativeSpeed { get; }
        public double ServerTime { get; }

        public ActorContactEvent(AuthoritativeActorHandle actorA, AuthoritativeActorHandle actorB, ActorContactSeverity severity, float directionX, float directionZ, float relativeSpeed, double serverTime)
        {
            ActorA = actorA; ActorB = actorB; Severity = severity; DirectionX = directionX; DirectionZ = directionZ;
            RelativeSpeed = relativeSpeed; ServerTime = serverTime;
        }
    }

    /// <summary>Classifies meaningful kinematic actor contact without rigidbody simulation.</summary>
    public sealed class ActorContactService
    {
        public event Action<ActorContactEvent> Contact;

        public ActorContactSeverity Classify(float relativeSpeed, ActorWeightClass a, ActorWeightClass b)
        {
            float speed = Math.Max(0f, relativeSpeed);
            int weight = Math.Max((int)a, (int)b);
            if (speed < 0.4f) return ActorContactSeverity.Brush;
            if (speed < 2.5f) return ActorContactSeverity.Bump;
            if (speed < 6f || weight >= (int)ActorWeightClass.Heavy) return ActorContactSeverity.HeavyImpact;
            return ActorContactSeverity.Knockdown;
        }

        public void Publish(ActorContactEvent contact)
        {
            if (contact.Severity != ActorContactSeverity.None)
                Contact?.Invoke(contact);
        }
    }
}
