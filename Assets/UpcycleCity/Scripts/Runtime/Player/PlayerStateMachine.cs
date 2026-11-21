using System;

namespace UpcycleCity.Player
{
    public interface IPlayerState
    {
        string Name { get; }
        void Enter();
        void Exit();
        void Tick(float dt);
        void FixedTick(float fixedDt);
    }

    public abstract class PlayerStateBase : IPlayerState
    {
        protected readonly PlayerController Player;
        protected readonly PlayerMotor Motor;
        protected readonly PlayerInputReader Input;
        protected readonly PlayerConfig Config;
        protected readonly PlayerStateMachine Machine;

        protected PlayerStateBase(PlayerController player, PlayerStateMachine machine)
        {
            Player = player;
            Motor = player.Motor;
            Input = player.Input;
            Config = player.Config;
            Machine = machine;
        }

        public abstract string Name { get; }
        public virtual void Enter() { }
        public virtual void Exit() { }
        public virtual void Tick(float dt) { }
        public virtual void FixedTick(float fixedDt) { }

        protected bool TryTransitionToJump()
        {
            if (Motor.HasBufferedJump && Motor.HasCoyoteTime)
            {
                Machine.TransitionTo(Machine.Jump);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Explicit Finite State Machine coordinating player locomotion states, with clean support
    /// for external override states (Locked/UI, machine repair hold, placement mode, riding bicycle).
    /// </summary>
    public sealed class PlayerStateMachine
    {
        readonly PlayerController player;

        public PlayerIdleState Idle { get; }
        public PlayerWalkState Walk { get; }
        public PlayerSprintState Sprint { get; }
        public PlayerCrouchState Crouch { get; }
        public PlayerJumpState Jump { get; }
        public PlayerFallState Fall { get; }
        public PlayerLockedState Locked { get; }

        public IPlayerState Current { get; private set; }
        public bool IsOverridden { get; private set; }
        public string CurrentName => Current != null ? Current.Name : "None";

        public event Action<IPlayerState, IPlayerState> StateChanged;

        public PlayerStateMachine(PlayerController controller)
        {
            player = controller;
            Idle = new PlayerIdleState(controller, this);
            Walk = new PlayerWalkState(controller, this);
            Sprint = new PlayerSprintState(controller, this);
            Crouch = new PlayerCrouchState(controller, this);
            Jump = new PlayerJumpState(controller, this);
            Fall = new PlayerFallState(controller, this);
            Locked = new PlayerLockedState(controller, this);
        }

        public void Start(IPlayerState initialState)
        {
            IsOverridden = false;
            Current = initialState;
            Current?.Enter();
            StateChanged?.Invoke(null, Current);
        }

        public void TransitionTo(IPlayerState nextState)
        {
            if (nextState == null || nextState == Current)
                return;

            IPlayerState previous = Current;
            previous?.Exit();
            Current = nextState;
            Current.Enter();
            StateChanged?.Invoke(previous, Current);
        }

        /// <summary>
        /// Enters an external override state (e.g. Locked for UI/cutscenes, RepairHold, RidingBike).
        /// Call <see cref="ExitOverrideState"/> to return cleanly to core locomotion.
        /// </summary>
        public void EnterOverrideState(IPlayerState customState)
        {
            if (customState == null)
                return;
            IsOverridden = true;
            TransitionTo(customState);
        }

        /// <summary>Exits any active override state and resumes normal grounded/air locomotion.</summary>
        public void ExitOverrideState()
        {
            if (!IsOverridden)
                return;
            IsOverridden = false;
            IPlayerState resume = player != null && player.Motor != null && !player.Motor.IsGrounded
                ? (IPlayerState)Fall
                : Idle;
            TransitionTo(resume);
        }

        public void Tick(float dt) => Current?.Tick(dt);

        public void FixedTick(float fixedDt) => Current?.FixedTick(fixedDt);
    }
}
