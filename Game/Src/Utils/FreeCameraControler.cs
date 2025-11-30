using Godot;
using System;

public partial class FreeCameraControler : Camera3D
{
	public float ShiftMultiplier = 45f;
	public float AltMultiplier = 1.0f / 2.5f;
	public float Sensitivity = 0.25f;

	//Mouse
	private Vector2 _mousePosition = new Vector2(0, 0);
	private float _totalPitch = 0f;
	//Movement
	private Vector3 _direction = new Vector3(0, 0, 0);
	private Vector3 _velocity = new Vector3(0, 0, 0);
	private float _acceleration = 30;
	private float _deceleration = -10;
	private float _velocityMultiplier = 4;

	//Keyboard
	private int _w = 0;
	private int _s = 0;
	private int _a = 0;
	private int _d = 0;
	private int _q = 0;
	private int _e = 0;
	private int _shift = 0;
	private int _alt = 0;

	public override void _UnhandledInput(InputEvent @event)
	{
		//Receives mouse motion
		if (@event is InputEventMouseMotion mouseMotion)
		{
			_mousePosition = mouseMotion.Relative;
		}
		//Receives mouse button input
		if (@event is InputEventMouseButton eventMouseButton)
		{
			if (eventMouseButton.ButtonIndex == MouseButton.Right)
			{
				if (eventMouseButton.Pressed)
				{
					Input.MouseMode = Input.MouseModeEnum.Captured;
					//Input.MouseMode = Input.MouseModeEnum.Hidden;
                }
				else
				{
					Input.MouseMode = Input.MouseModeEnum.Visible;
				}
			}
			else if (eventMouseButton.ButtonIndex == MouseButton.WheelUp)
			{
				_velocityMultiplier = Mathf.Clamp(_velocityMultiplier * 1.1f, 0.2f, 200f);
			}
			else if (eventMouseButton.ButtonIndex == MouseButton.WheelDown)
			{
				_velocityMultiplier = Mathf.Clamp(_velocityMultiplier / 1.1f, 0.2f, 200f);
			}
		}
		//Receives key input
		if (@event is InputEventKey eventKey)
		{
			if (eventKey.Keycode == Key.W)
			{
				_w = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.S)
			{
				_s = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.A)
			{
				_a = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.D)
			{
				_d = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.Q)
			{
				_q = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.E)
			{
				_e = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.Shift)
			{
				_shift = eventKey.Pressed ? 1 : 0;
			}
			if (eventKey.Keycode == Key.Alt)
			{
				_alt = eventKey.Pressed ? 1 : 0;
			}
		}
	}
	private void UpdateMovement(float delta)
	{
		_direction = new Vector3(_d - _a, _e - _q, _s - _w);
		//Computes the change in velocity due to desired direction and "drag"
		// The "drag" is a constant acceleration on the camera to bring it's velocity to 0

		Vector3 offset = _direction.Normalized() * _acceleration * _velocityMultiplier * delta +
			_velocity.Normalized() * _deceleration * _velocityMultiplier * delta;

		//Compute modifiers' speed multiplier
		var speedMulti = 1f;
		if (_shift != 0)
		{
			speedMulti *= ShiftMultiplier;
		}
		if (_alt != 0)
		{
			speedMulti *= AltMultiplier;
		}
		//Checks if we should bother translating the camera
		if (_direction == Vector3.Zero && offset.LengthSquared() > _velocity.LengthSquared())
		{
			_velocity = Vector3.Zero;
		}
		else
		{
			_velocity.X = Mathf.Clamp(_velocity.X + offset.X, - _velocityMultiplier, _velocityMultiplier);
			_velocity.Y = Mathf.Clamp(_velocity.Y + offset.Y, - _velocityMultiplier, _velocityMultiplier);
			_velocity.Z = Mathf.Clamp(_velocity.Z + offset.Z, - _velocityMultiplier, _velocityMultiplier);

			Translate(_velocity * delta * speedMulti);
		}

	}
	private void UpdateMouseLook()
	{
		//Only rotates mouse if the mouse is captured
		if (Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			_mousePosition *= Sensitivity;
			var yaw = _mousePosition.X;
			var pitch = _mousePosition.Y;
			_mousePosition = Vector2.Zero;

			//Prevents looking up/down too far
			pitch = Mathf.Clamp(pitch, -90 - _totalPitch, 90 - _totalPitch);
			_totalPitch += pitch;

			RotateY(Mathf.DegToRad(-yaw));
			RotateObjectLocal(new Vector3(1, 0, 0), Mathf.DegToRad(-pitch));
		}
	}
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	public override void _Process(double delta)
	{
		//base._PhysicsProcess(delta);
		UpdateMouseLook();
		UpdateMovement((float)delta);
	}
}
