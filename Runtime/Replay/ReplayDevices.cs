using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.InputSystem.XR;

namespace XRSessionCapture
{
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    public struct ReplayHMDState : IInputStateTypeInfo
    {
        public static FourCC Format => new FourCC('X', 'R', 'S', 'H');
        public FourCC format => Format;

        [InputControl(usage = "DevicePosition")] [FieldOffset(0)] public Vector3 devicePosition;
        [InputControl(usage = "DeviceRotation")] [FieldOffset(12)] public Quaternion deviceRotation;
        [InputControl(usage = "CenterEyePosition")] [FieldOffset(28)] public Vector3 centerEyePosition;
        [InputControl(usage = "CenterEyeRotation")] [FieldOffset(40)] public Quaternion centerEyeRotation;
        [InputControl] [FieldOffset(56)] public Vector3 leftEyePosition;
        [InputControl] [FieldOffset(68)] public Quaternion leftEyeRotation;
        [InputControl] [FieldOffset(84)] public Vector3 rightEyePosition;
        [InputControl] [FieldOffset(96)] public Quaternion rightEyeRotation;
        [InputControl(usage = "TrackingState", layout = "Integer")] [FieldOffset(112)] public int trackingState;
        [InputControl(name = "isTracked", usage = "IsTracked", layout = "Button", bit = 0)] [FieldOffset(116)] public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 88)]
    public struct ReplayControllerState : IInputStateTypeInfo
    {
        public static FourCC Format => new FourCC('X', 'R', 'S', 'C');
        public FourCC format => Format;

        [InputControl(usage = "DevicePosition")] [FieldOffset(0)] public Vector3 devicePosition;
        [InputControl(usage = "DeviceRotation")] [FieldOffset(12)] public Quaternion deviceRotation;
        [InputControl] [FieldOffset(28)] public Vector3 pointerPosition;
        [InputControl] [FieldOffset(40)] public Quaternion pointerRotation;
        [InputControl(usage = "Trigger", layout = "Axis")] [FieldOffset(56)] public float trigger;
        [InputControl(usage = "Grip", layout = "Axis")] [FieldOffset(60)] public float grip;
        [InputControl(usage = "Primary2DAxis", aliases = new[] { "thumbstick", "joystick" })] [FieldOffset(64)] public Vector2 primary2DAxis;
        [InputControl(usage = "TrackingState", layout = "Integer")] [FieldOffset(72)] public int trackingState;

        [InputControl(name = "triggerPressed", usage = "TriggerButton", aliases = new[] { "triggerButton" }, layout = "Button", bit = 0)]
        [InputControl(name = "gripPressed", usage = "GripButton", aliases = new[] { "gripButton" }, layout = "Button", bit = 1)]
        [InputControl(name = "primaryButton", usage = "PrimaryButton", layout = "Button", bit = 2)]
        [InputControl(name = "secondaryButton", usage = "SecondaryButton", layout = "Button", bit = 3)]
        [InputControl(name = "menu", usages = new[] { "MenuButton", "Menu" }, aliases = new[] { "menuButton" }, layout = "Button", bit = 4)]
        [InputControl(name = "primary2DAxisClick", usage = "Primary2DAxisClick", aliases = new[] { "thumbstickClicked", "joystickClicked" }, layout = "Button", bit = 5)]
        [InputControl(name = "primary2DAxisTouch", usage = "Primary2DAxisTouch", aliases = new[] { "thumbstickTouched", "joystickTouched" }, layout = "Button", bit = 6)]
        [InputControl(name = "primaryTouched", usage = "PrimaryTouch", layout = "Button", bit = 7)]
        [InputControl(name = "secondaryTouched", usage = "SecondaryTouch", layout = "Button", bit = 8)]
        [InputControl(name = "triggerTouched", usage = "TriggerTouch", layout = "Button", bit = 9)]
        [InputControl(name = "isTracked", usage = "IsTracked", layout = "Button", bit = 31)]
        [FieldOffset(76)]
        public uint buttons;

        public const uint IsTrackedBit = 1u << 31;
    }

    /// <summary>Virtual headset fed by <see cref="SessionPlayer"/>. Bindings to &lt;XRHMD&gt; (TrackedPoseDriver, XR Interaction Toolkit) pick it up.</summary>
    [InputControlLayout(stateType = typeof(ReplayHMDState), displayName = "XR Session Replay HMD", canRunInBackground = true, hideInUI = true)]
    public class ReplayHMD : XRHMD
    {
    }

    /// <summary>Virtual controller fed by <see cref="SessionPlayer"/>. Bindings to &lt;XRController&gt;{LeftHand}/{RightHand} pick it up.</summary>
    [InputControlLayout(stateType = typeof(ReplayControllerState), commonUsages = new[] { "LeftHand", "RightHand" }, displayName = "XR Session Replay Controller", hideInUI = true)]
    public class ReplayController : XRController
    {
    }

    public static class ReplayLayouts
    {
        static bool s_Registered;

        public static void Register()
        {
            if (s_Registered)
                return;
            s_Registered = true;
            InputSystem.RegisterLayout<ReplayHMD>();
            InputSystem.RegisterLayout<ReplayController>();
        }

        internal static ReplayHMDState ToState(in HeadSample head)
        {
            var p = head.pose.position;
            var r = PoseSample.Normalize(head.pose.rotation);
            return new ReplayHMDState
            {
                devicePosition = p,
                deviceRotation = r,
                centerEyePosition = p,
                centerEyeRotation = r,
                leftEyePosition = p,
                leftEyeRotation = r,
                rightEyePosition = p,
                rightEyeRotation = r,
                trackingState = head.trackingState,
                flags = head.isTracked ? 1u : 0u,
            };
        }

        internal static ReplayControllerState ToState(in ControllerSample c)
        {
            var buttons = (uint)c.buttons & 0x3FF;
            if (c.isTracked)
                buttons |= ReplayControllerState.IsTrackedBit;
            return new ReplayControllerState
            {
                devicePosition = c.device.position,
                deviceRotation = PoseSample.Normalize(c.device.rotation),
                pointerPosition = c.pointer.position,
                pointerRotation = PoseSample.Normalize(c.pointer.rotation),
                trigger = c.trigger,
                grip = c.grip,
                primary2DAxis = c.stick,
                trackingState = c.trackingState,
                buttons = buttons,
            };
        }
    }
}
