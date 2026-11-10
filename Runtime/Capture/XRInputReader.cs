using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.InputSystem.XR;

namespace XRSessionCapture
{
    /// <summary>
    /// Reads the headset and controllers from the Input System. Controls are looked up by name and then by usage,
    /// so it works with the VIVE Focus 3 OpenXR profile and with any other OpenXR controller profile.
    /// Replay devices are ignored, so recording while replaying is not possible by accident.
    /// </summary>
    public static class XRInputReader
    {
        sealed class ControllerControls
        {
            public Vector3Control devicePosition, pointerPosition;
            public QuaternionControl deviceRotation, pointerRotation;
            public IntegerControl trackingState;
            public ButtonControl isTracked;
            public AxisControl trigger, grip;
            public Vector2Control stick;
            public ButtonControl triggerPressed, gripPressed, primary, secondary, menu, stickClick, stickTouch, primaryTouch, secondaryTouch, triggerTouch;
        }

        static readonly Dictionary<InputDevice, ControllerControls> s_Cache = new Dictionary<InputDevice, ControllerControls>();

        public static XRHMD FindHeadset()
        {
            XRHMD found = null;
            foreach (var device in InputSystem.devices)
            {
                if (device is XRHMD hmd && !(device is ReplayHMD) && device.enabled && device.added)
                {
                    // Prefer the most recently updated one if there are several.
                    if (found == null || hmd.lastUpdateTime > found.lastUpdateTime)
                        found = hmd;
                }
            }
            return found;
        }

        public static XRController FindController(bool left)
        {
            var usage = left ? CommonUsages.LeftHand : CommonUsages.RightHand;
            XRController found = null;
            foreach (var device in InputSystem.devices)
            {
                if (device is XRController controller && !(device is ReplayController) && device.enabled && device.added && HasUsage(device, usage))
                {
                    if (found == null || controller.lastUpdateTime > found.lastUpdateTime)
                        found = controller;
                }
            }
            return found;
        }

        public static HeadSample ReadHead()
        {
            var hmd = FindHeadset();
            if (hmd == null)
                return new HeadSample { pose = PoseSample.Identity };
            var position = hmd.centerEyePosition ?? hmd.devicePosition;
            var rotation = hmd.centerEyeRotation ?? hmd.deviceRotation;
            return new HeadSample
            {
                isTracked = hmd.isTracked != null && hmd.isTracked.isPressed,
                trackingState = hmd.trackingState != null ? hmd.trackingState.ReadValue() : 0,
                pose = new PoseSample(position != null ? position.ReadValue() : Vector3.zero, rotation != null ? PoseSample.Normalize(rotation.ReadValue()) : Quaternion.identity),
            };
        }

        public static ControllerSample ReadController(bool left)
        {
            var device = FindController(left);
            if (device == null)
                return default;
            var c = GetControls(device);
            var devicePose = new PoseSample(Read(c.devicePosition), ReadRotation(c.deviceRotation));
            var sample = new ControllerSample
            {
                connected = true,
                isTracked = c.isTracked != null && c.isTracked.isPressed,
                trackingState = c.trackingState != null ? c.trackingState.ReadValue() : 0,
                device = devicePose,
                pointer = c.pointerPosition != null && c.pointerRotation != null
                    ? new PoseSample(c.pointerPosition.ReadValue(), ReadRotation(c.pointerRotation))
                    : devicePose,
                trigger = c.trigger != null ? c.trigger.ReadValue() : 0,
                grip = c.grip != null ? c.grip.ReadValue() : 0,
                stick = c.stick != null ? c.stick.ReadValue() : Vector2.zero,
            };
            var b = ControllerButtons.None;
            if (Pressed(c.triggerPressed) || (c.triggerPressed == null && sample.trigger > 0.5f)) b |= ControllerButtons.TriggerPressed;
            if (Pressed(c.gripPressed) || (c.gripPressed == null && sample.grip > 0.5f)) b |= ControllerButtons.GripPressed;
            if (Pressed(c.primary)) b |= ControllerButtons.Primary;
            if (Pressed(c.secondary)) b |= ControllerButtons.Secondary;
            if (Pressed(c.menu)) b |= ControllerButtons.Menu;
            if (Pressed(c.stickClick)) b |= ControllerButtons.StickClick;
            if (Pressed(c.stickTouch)) b |= ControllerButtons.StickTouch;
            if (Pressed(c.primaryTouch)) b |= ControllerButtons.PrimaryTouch;
            if (Pressed(c.secondaryTouch)) b |= ControllerButtons.SecondaryTouch;
            if (Pressed(c.triggerTouch)) b |= ControllerButtons.TriggerTouch;
            sample.buttons = b;
            return sample;
        }

        static bool Pressed(ButtonControl button) => button != null && button.isPressed;

        static Vector3 Read(Vector3Control control) => control != null ? control.ReadValue() : Vector3.zero;

        static Quaternion ReadRotation(QuaternionControl control) => control != null ? PoseSample.Normalize(control.ReadValue()) : Quaternion.identity;

        static ControllerControls GetControls(InputDevice device)
        {
            if (s_Cache.TryGetValue(device, out var cached))
                return cached;
            var c = new ControllerControls
            {
                devicePosition = Find<Vector3Control>(device, new[] { "devicePosition", "devicePose/position", "gripPosition" }, "DevicePosition"),
                deviceRotation = Find<QuaternionControl>(device, new[] { "deviceRotation", "devicePose/rotation", "gripRotation" }, "DeviceRotation"),
                pointerPosition = Find<Vector3Control>(device, new[] { "pointerPosition", "pointer/position", "aimPosition" }),
                pointerRotation = Find<QuaternionControl>(device, new[] { "pointerRotation", "pointer/rotation", "aimRotation" }),
                trackingState = Find<IntegerControl>(device, new[] { "trackingState" }, "TrackingState"),
                isTracked = Find<ButtonControl>(device, new[] { "isTracked" }, "IsTracked"),
                trigger = Find<AxisControl>(device, new[] { "trigger" }, "Trigger"),
                grip = Find<AxisControl>(device, new[] { "grip" }, "Grip"),
                stick = Find<Vector2Control>(device, new[] { "thumbstick", "primary2DAxis", "joystick" }, "Primary2DAxis"),
                triggerPressed = Find<ButtonControl>(device, new[] { "triggerPressed", "triggerButton" }, "TriggerButton"),
                gripPressed = Find<ButtonControl>(device, new[] { "gripPressed", "gripButton" }, "GripButton"),
                primary = Find<ButtonControl>(device, new[] { "primaryButton" }, "PrimaryButton"),
                secondary = Find<ButtonControl>(device, new[] { "secondaryButton" }, "SecondaryButton"),
                menu = Find<ButtonControl>(device, new[] { "menu", "menuButton" }, "MenuButton", "Menu"),
                stickClick = Find<ButtonControl>(device, new[] { "thumbstickClicked", "primary2DAxisClick", "joystickClicked" }, "Primary2DAxisClick"),
                stickTouch = Find<ButtonControl>(device, new[] { "thumbstickTouched", "primary2DAxisTouch", "joystickTouched" }, "Primary2DAxisTouch"),
                primaryTouch = Find<ButtonControl>(device, new[] { "primaryTouched" }, "PrimaryTouch"),
                secondaryTouch = Find<ButtonControl>(device, new[] { "secondaryTouched" }, "SecondaryTouch"),
                triggerTouch = Find<ButtonControl>(device, new[] { "triggerTouched" }, "TriggerTouch"),
            };
            s_Cache[device] = c;
            if (s_Cache.Count > 16)
                PruneCache();
            return c;
        }

        static void PruneCache()
        {
            var removed = new List<InputDevice>();
            foreach (var device in s_Cache.Keys)
                if (!device.added)
                    removed.Add(device);
            foreach (var device in removed)
                s_Cache.Remove(device);
        }

        static T Find<T>(InputDevice device, string[] names, params string[] usages) where T : InputControl
        {
            foreach (var name in names)
            {
                if (device.TryGetChildControl(name) is T byName)
                    return byName;
            }
            foreach (var usage in usages)
            {
                var interned = new InternedString(usage);
                foreach (var control in device.allControls)
                {
                    if (control is T typed && control.usages.Contains(interned))
                        return typed;
                }
            }
            return null;
        }

        internal static bool HasUsage(InputDevice device, InternedString usage)
        {
            foreach (var u in device.usages)
                if (u == usage)
                    return true;
            return false;
        }
    }
}
