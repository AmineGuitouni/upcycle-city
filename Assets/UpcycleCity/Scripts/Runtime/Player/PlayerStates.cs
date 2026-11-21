using UnityEngine;

namespace UpcycleCity.Player
{
    public sealed class PlayerIdleState : PlayerStateBase
    {
        public PlayerIdleState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Idle";

        public override void Tick(float dt)
        {
            if (TryTransitionToJump())
                return;
            if (!Motor.IsGrounded)
            {
                Machine.TransitionTo(Machine.Fall);
                return;
            }
            if (Input.CrouchHeld || !Motor.CanStandUp())
            {
                Machine.TransitionTo(Machine.Crouch);
                return;
            }
            if (Input.Move.sqrMagnitude > 0.01f)
            {
                bool wantsSprint = Input.SprintHeld && !Motor.SprintBlocked && Input.Move.y > 0.1f;
                Machine.TransitionTo(wantsSprint ? (IPlayerState)Machine.Sprint : Machine.Walk);
            }
        }

        public override void FixedTick(float fixedDt)
        {
            Motor.SetCrouch(false, fixedDt);
            Motor.MoveHorizontal(Vector3.zero, 0f, Config.groundAcceleration, Config.groundDeceleration, fixedDt);
        }
    }

    public sealed class PlayerWalkState : PlayerStateBase
    {
        public PlayerWalkState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Walk";

        public override void Tick(float dt)
        {
            if (TryTransitionToJump())
                return;
            if (!Motor.IsGrounded)
            {
                Machine.TransitionTo(Machine.Fall);
                return;
            }
            if (Input.CrouchHeld || !Motor.CanStandUp())
            {
                Machine.TransitionTo(Machine.Crouch);
                return;
            }
            if (Input.Move.sqrMagnitude <= 0.01f)
            {
                Machine.TransitionTo(Machine.Idle);
                return;
            }
            if (Input.SprintHeld && !Motor.SprintBlocked && Input.Move.y > 0.1f)
                Machine.TransitionTo(Machine.Sprint);
        }

        public override void FixedTick(float fixedDt)
        {
            Motor.SetCrouch(false, fixedDt);
            Vector3 wish = Motor.GetWishDirection(Input.Move);
            Motor.MoveHorizontal(wish, Config.walkSpeed, Config.groundAcceleration, Config.groundDeceleration, fixedDt);
        }
    }

    public sealed class PlayerSprintState : PlayerStateBase
    {
        public PlayerSprintState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Sprint";

        public override void Tick(float dt)
        {
            if (TryTransitionToJump())
                return;
            if (!Motor.IsGrounded)
            {
                Machine.TransitionTo(Machine.Fall);
                return;
            }
            if (Input.CrouchHeld || !Motor.CanStandUp())
            {
                Machine.TransitionTo(Machine.Crouch);
                return;
            }
            if (Input.Move.sqrMagnitude <= 0.01f)
            {
                Machine.TransitionTo(Machine.Idle);
                return;
            }
            if (!Input.SprintHeld || Motor.SprintBlocked || Input.Move.y <= 0.1f)
                Machine.TransitionTo(Machine.Walk);
        }

        public override void FixedTick(float fixedDt)
        {
            Motor.SetCrouch(false, fixedDt);
            Vector3 wish = Motor.GetWishDirection(Input.Move);
            Motor.MoveHorizontal(wish, Config.sprintSpeed, Config.groundAcceleration, Config.groundDeceleration, fixedDt);
        }
    }

    public sealed class PlayerCrouchState : PlayerStateBase
    {
        public PlayerCrouchState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Crouch";

        public override void Tick(float dt)
        {
            if (Motor.CanStandUp() && TryTransitionToJump())
                return;
            if (!Motor.IsGrounded)
            {
                Machine.TransitionTo(Machine.Fall);
                return;
            }
            if (!Input.CrouchHeld && Motor.CanStandUp())
            {
                Machine.TransitionTo(Input.Move.sqrMagnitude > 0.01f ? (IPlayerState)Machine.Walk : Machine.Idle);
            }
        }

        public override void FixedTick(float fixedDt)
        {
            Motor.SetCrouch(true, fixedDt);
            Vector3 wish = Motor.GetWishDirection(Input.Move);
            Motor.MoveHorizontal(wish, Config.crouchSpeed, Config.groundAcceleration, Config.groundDeceleration, fixedDt);
        }
    }

    public sealed class PlayerJumpState : PlayerStateBase
    {
        bool jumpCutApplied;

        public PlayerJumpState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Jump";

        public override void Enter()
        {
            jumpCutApplied = false;
            Motor.LaunchJump();
        }

        public override void Tick(float dt)
        {
            if (!jumpCutApplied && Input.JumpReleasedThisFrame)
            {
                jumpCutApplied = true;
                Motor.CutJumpVelocity();
            }

            if (Motor.VerticalSpeed <= 0f)
                Machine.TransitionTo(Machine.Fall);
        }

        public override void FixedTick(float fixedDt)
        {
            Motor.SetCrouch(Input.CrouchHeld, fixedDt);
            float airTargetSpeed = Input.SprintHeld && !Motor.SprintBlocked ? Config.sprintSpeed : Config.walkSpeed;
            Vector3 wish = Motor.GetWishDirection(Input.Move);
            Motor.MoveHorizontal(wish, airTargetSpeed, Config.airAcceleration, Config.airDeceleration, fixedDt);
            Motor.ApplyGravity(fixedDt, 1f);
        }
    }

    public sealed class PlayerFallState : PlayerStateBase
    {
        public PlayerFallState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Fall";

        public override void Tick(float dt)
        {
            // Coyote jump if the player just walked off a ledge
            if (TryTransitionToJump())
                return;

            if (Motor.IsGrounded)
            {
                if (Input.CrouchHeld || !Motor.CanStandUp())
                    Machine.TransitionTo(Machine.Crouch);
                else if (Input.Move.sqrMagnitude <= 0.01f)
                    Machine.TransitionTo(Machine.Idle);
                else if (Input.SprintHeld && !Motor.SprintBlocked && Input.Move.y > 0.1f)
                    Machine.TransitionTo(Machine.Sprint);
                else
                    Machine.TransitionTo(Machine.Walk);
            }
        }

        public override void FixedTick(float fixedDt)
        {
            Motor.SetCrouch(Input.CrouchHeld, fixedDt);
            float airTargetSpeed = Input.SprintHeld && !Motor.SprintBlocked ? Config.sprintSpeed : Config.walkSpeed;
            Vector3 wish = Motor.GetWishDirection(Input.Move);
            Motor.MoveHorizontal(wish, airTargetSpeed, Config.airAcceleration, Config.airDeceleration, fixedDt);
            Motor.ApplyGravity(fixedDt, Config.fallGravityMultiplier);
        }
    }

    /// <summary>
    /// Safe locked state for UI modals, tutorials, dialogue, or timed interactions.
    /// Smoothly brakes horizontal motion while keeping gravity intact.
    /// </summary>
    public sealed class PlayerLockedState : PlayerStateBase
    {
        public PlayerLockedState(PlayerController player, PlayerStateMachine machine) : base(player, machine) { }
        public override string Name => "Locked";

        public override void FixedTick(float fixedDt)
        {
            Motor.MoveHorizontal(Vector3.zero, 0f, Config.groundAcceleration, Config.groundDeceleration, fixedDt);
            if (!Motor.IsGrounded)
                Motor.ApplyGravity(fixedDt, Config.fallGravityMultiplier);
        }
    }
}
