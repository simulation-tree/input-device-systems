using InputDevices.Components;
using SDL3;
using SharpHook;
using SharpHook.Native;
using Simulation;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Worlds;

namespace InputDevices.Systems
{
    public partial struct GlobalKeyboardAndMouseSystem : ISystem
    {
        private static bool globalMouseMoved;
        private static bool globalMouseScrolled;
        private static KeyboardState globalCurrentKeyboard = default;
        private static KeyboardState globalLastKeyboard = default;
        private static MouseState globalCurrentMouse = default;
        private static MouseState globalLastMouse = default;
        private static Vector2 globalMousePosition;
        private static Vector2 globalMouseScroll;

        private readonly Simulator simulator;
        private GlobalHook kbmHook;
        private uint globalKeyboardEntity;
        private uint globalMouseEntity;
        private uint screenWidth;
        private uint screenHeight;
        private unsafe delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction;

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                systemContainer.Write(new GlobalKeyboardAndMouseSystem(systemContainer.simulator));
            }
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            Update(world);
        }

        unsafe void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                if (kbmHook != default)
                {
                    kbmHook.Dispose();
                }

                SDL3.SDL3.SDL_RemoveEventWatch(eventFilterFunction, simulator.Address);
            }
        }

        private unsafe GlobalKeyboardAndMouseSystem(Simulator simulator)
        {
            this.simulator = simulator;
            eventFilterFunction = &EventFilter;
            SDL3.SDL3.SDL_AddEventWatch(eventFilterFunction, simulator.Address);
        }

        private void Update(World world)
        {
            FindGlobalDevices(world);
            UpdateStates(world);
        }

        private void FindGlobalDevices(World world)
        {
            globalKeyboardEntity = default;
            globalMouseEntity = default;
            ComponentQuery<IsKeyboard, IsGlobal> globalKeyboardsQuery = new(world);
            foreach (var r in globalKeyboardsQuery)
            {
                if (globalKeyboardEntity != default)
                {
                    throw new InvalidOperationException("Multiple global keyboard entities found");
                }

                globalKeyboardEntity = r.entity;
            }

            ComponentQuery<IsMouse, IsGlobal> globalMiceQuery = new(world);
            foreach (var r in globalMiceQuery)
            {
                if (globalMouseEntity != default)
                {
                    throw new InvalidOperationException("Multiple global mouse entities found");
                }

                globalMouseEntity = r.entity;
            }

            if (kbmHook == default)
            {
                if (globalKeyboardEntity != default || globalMouseEntity != default)
                {
                    kbmHook = new(InitializeGlobalHook());
                    Trace.WriteLine("Global keyboard and mouse hook initialized");
                }
            }
        }

        private readonly TaskPoolGlobalHook InitializeGlobalHook()
        {
            TaskPoolGlobalHook kbmHook = new();
            kbmHook.KeyPressed += OnKeyPressed;
            kbmHook.KeyReleased += OnKeyReleased;
            kbmHook.MousePressed += OnMousePressed;
            kbmHook.MouseReleased += OnMouseReleased;
            kbmHook.MouseDragged += OnMouseDragged;
            kbmHook.MouseMoved += OnMouseMoved;
            kbmHook.MouseWheel += OnMouseWheel;
            kbmHook.RunAsync();
            return kbmHook;
        }

        private readonly void UpdateStates(World world)
        {
            if (globalKeyboardEntity != default)
            {
                Keyboard keyboard = new(world, globalKeyboardEntity);
                bool keyboardUpdated = false;
                for (uint i = 0; i < KeyboardState.MaxKeyCount; i++)
                {
                    bool next = globalCurrentKeyboard[i];
                    bool previous = globalLastKeyboard[i];
                    ButtonState state = keyboard.GetButtonState(i);
                    ButtonState current = new(previous, next);
                    if (state != current)
                    {
                        globalLastKeyboard[i] = next;
                        keyboard.SetButtonState(i, current);
                        keyboardUpdated = true;
                    }
                }

                if (keyboardUpdated)
                {
                    DateTimeOffset when = DateTimeOffset.Now;
                    TimeSpan timestamp = when - DateTimeOffset.UnixEpoch;
                    InputDevice device = keyboard;
                    device.SetUpdateTime(timestamp);
                }
            }

            if (globalMouseEntity != default)
            {
                Mouse mouse = new(world, globalMouseEntity);
                bool mouseUpdated = false;
                for (uint i = 0; i < MouseState.MaxButtonCount; i++)
                {
                    bool next = globalCurrentMouse[i];
                    bool previous = globalLastMouse[i];
                    ButtonState state = mouse.GetButtonState(i);
                    ButtonState current = new(previous, next);
                    if (state != current)
                    {
                        globalLastMouse[i] = next;
                        mouse.SetButtonState(i, current);
                        mouseUpdated = true;
                    }
                }

                if (globalMouseMoved)
                {
                    mouse.Position = globalMousePosition;
                    mouseUpdated = true;
                }

                if (globalMouseScrolled)
                {
                    mouse.Scroll = globalMouseScroll;
                    mouseUpdated = true;
                }

                if (mouseUpdated)
                {
                    DateTimeOffset when = DateTimeOffset.Now;
                    TimeSpan timestamp = when - DateTimeOffset.UnixEpoch;
                    InputDevice device = mouse;
                    device.SetUpdateTime(timestamp);
                }

                globalMouseMoved = false;
                globalMouseScrolled = false;
            }
        }

        private readonly void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
        {
            if (simulator != default && e.Data.KeyCode != KeyCode.VcUndefined)
            {
                Keyboard.Button control = GetControl(e.Data.KeyCode);
                if (!globalCurrentKeyboard[(uint)control])
                {
                    globalCurrentKeyboard[(uint)control] = true;
                }
            }
        }

        private readonly void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            if (simulator != default && e.Data.KeyCode != KeyCode.VcUndefined)
            {
                Keyboard.Button control = GetControl(e.Data.KeyCode);
                if (globalCurrentKeyboard[(uint)control])
                {
                    globalCurrentKeyboard[(uint)control] = false;
                }
            }
        }

        private readonly void OnMousePressed(object? sender, MouseHookEventArgs e)
        {
            if (simulator != default)
            {
                uint control = (uint)e.Data.Button;
                if (!globalCurrentMouse[control])
                {
                    globalCurrentMouse[control] = true;
                }
            }
        }

        private readonly void OnMouseReleased(object? sender, MouseHookEventArgs e)
        {
            if (simulator != default)
            {
                uint control = (uint)e.Data.Button;
                if (globalCurrentMouse[control])
                {
                    globalCurrentMouse[control] = false;
                }
            }
        }

        private readonly void OnMouseDragged(object? sender, MouseHookEventArgs e)
        {
            if (simulator != default)
            {
                globalMousePosition = new Vector2(e.Data.X, e.Data.Y);
                globalMouseMoved = true;
            }
        }

        private readonly void OnMouseMoved(object? sender, MouseHookEventArgs e)
        {
            if (simulator != default)
            {
                globalMousePosition = new Vector2(e.Data.X, e.Data.Y);
                globalMouseMoved = true;
            }
        }

        private readonly void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
        {
            if (simulator != default)
            {
                globalMouseScroll = new Vector2(e.Data.X, e.Data.Y);
                globalMouseScrolled = true;
            }
        }

        private static Keyboard.Button GetControl(KeyCode keyCode)
        {
            return keyCode switch
            {
                KeyCode.Vc0 => Keyboard.Button.Digit0,
                KeyCode.Vc1 => Keyboard.Button.Digit1,
                KeyCode.Vc2 => Keyboard.Button.Digit2,
                KeyCode.Vc3 => Keyboard.Button.Digit3,
                KeyCode.Vc4 => Keyboard.Button.Digit4,
                KeyCode.Vc5 => Keyboard.Button.Digit5,
                KeyCode.Vc6 => Keyboard.Button.Digit6,
                KeyCode.Vc7 => Keyboard.Button.Digit7,
                KeyCode.Vc8 => Keyboard.Button.Digit8,
                KeyCode.Vc9 => Keyboard.Button.Digit9,
                KeyCode.VcA => Keyboard.Button.A,
                KeyCode.VcB => Keyboard.Button.B,
                KeyCode.VcC => Keyboard.Button.C,
                KeyCode.VcD => Keyboard.Button.D,
                KeyCode.VcE => Keyboard.Button.E,
                KeyCode.VcF => Keyboard.Button.F,
                KeyCode.VcG => Keyboard.Button.G,
                KeyCode.VcH => Keyboard.Button.H,
                KeyCode.VcI => Keyboard.Button.I,
                KeyCode.VcJ => Keyboard.Button.J,
                KeyCode.VcK => Keyboard.Button.K,
                KeyCode.VcL => Keyboard.Button.L,
                KeyCode.VcM => Keyboard.Button.M,
                KeyCode.VcN => Keyboard.Button.N,
                KeyCode.VcO => Keyboard.Button.O,
                KeyCode.VcP => Keyboard.Button.P,
                KeyCode.VcQ => Keyboard.Button.Q,
                KeyCode.VcR => Keyboard.Button.R,
                KeyCode.VcS => Keyboard.Button.S,
                KeyCode.VcT => Keyboard.Button.T,
                KeyCode.VcU => Keyboard.Button.U,
                KeyCode.VcV => Keyboard.Button.V,
                KeyCode.VcW => Keyboard.Button.W,
                KeyCode.VcX => Keyboard.Button.X,
                KeyCode.VcY => Keyboard.Button.Y,
                KeyCode.VcZ => Keyboard.Button.Z,
                KeyCode.VcF1 => Keyboard.Button.F1,
                KeyCode.VcF2 => Keyboard.Button.F2,
                KeyCode.VcF3 => Keyboard.Button.F3,
                KeyCode.VcF4 => Keyboard.Button.F4,
                KeyCode.VcF5 => Keyboard.Button.F5,
                KeyCode.VcF6 => Keyboard.Button.F6,
                KeyCode.VcF7 => Keyboard.Button.F7,
                KeyCode.VcF8 => Keyboard.Button.F8,
                KeyCode.VcF9 => Keyboard.Button.F9,
                KeyCode.VcF10 => Keyboard.Button.F10,
                KeyCode.VcF11 => Keyboard.Button.F11,
                KeyCode.VcF12 => Keyboard.Button.F12,
                KeyCode.VcF13 => Keyboard.Button.F13,
                KeyCode.VcF14 => Keyboard.Button.F14,
                KeyCode.VcF15 => Keyboard.Button.F15,
                KeyCode.VcF16 => Keyboard.Button.F16,
                KeyCode.VcF17 => Keyboard.Button.F17,
                KeyCode.VcF18 => Keyboard.Button.F18,
                KeyCode.VcF19 => Keyboard.Button.F19,
                KeyCode.VcF20 => Keyboard.Button.F20,
                KeyCode.VcF21 => Keyboard.Button.F21,
                KeyCode.VcF22 => Keyboard.Button.F22,
                KeyCode.VcF23 => Keyboard.Button.F23,
                KeyCode.VcF24 => Keyboard.Button.F24,
                KeyCode.VcNumLock => Keyboard.Button.NumLock,
                KeyCode.VcScrollLock => Keyboard.Button.ScrollLock,
                KeyCode.VcLeftShift => Keyboard.Button.LeftShift,
                KeyCode.VcRightShift => Keyboard.Button.RightShift,
                KeyCode.VcLeftControl => Keyboard.Button.LeftControl,
                KeyCode.VcRightControl => Keyboard.Button.RightControl,
                KeyCode.VcLeftAlt => Keyboard.Button.LeftAlt,
                KeyCode.VcRightAlt => Keyboard.Button.RightAlt,
                KeyCode.VcLeftMeta => Keyboard.Button.LeftGui,
                KeyCode.VcRightMeta => Keyboard.Button.RightGui,
                KeyCode.VcSpace => Keyboard.Button.Space,
                KeyCode.VcQuote => Keyboard.Button.Apostrophe,
                KeyCode.VcComma => Keyboard.Button.Comma,
                KeyCode.VcMinus => Keyboard.Button.Minus,
                KeyCode.VcPeriod => Keyboard.Button.Period,
                KeyCode.VcSlash => Keyboard.Button.Slash,
                KeyCode.VcSemicolon => Keyboard.Button.Semicolon,
                KeyCode.VcEquals => Keyboard.Button.Equals,
                KeyCode.VcOpenBracket => Keyboard.Button.LeftBracket,
                KeyCode.VcBackslash => Keyboard.Button.Backslash,
                KeyCode.VcCloseBracket => Keyboard.Button.RightBracket,
                KeyCode.VcBackQuote => Keyboard.Button.Grave,
                KeyCode.VcEscape => Keyboard.Button.Escape,
                KeyCode.VcEnter => Keyboard.Button.Enter,
                KeyCode.VcTab => Keyboard.Button.Tab,
                KeyCode.VcBackspace => Keyboard.Button.Backspace,
                KeyCode.VcInsert => Keyboard.Button.Insert,
                KeyCode.VcDelete => Keyboard.Button.Delete,
                KeyCode.VcRight => Keyboard.Button.Right,
                KeyCode.VcLeft => Keyboard.Button.Left,
                KeyCode.VcDown => Keyboard.Button.Down,
                KeyCode.VcUp => Keyboard.Button.Up,
                KeyCode.VcHome => Keyboard.Button.Home,
                KeyCode.VcEnd => Keyboard.Button.End,
                _ => throw new NotImplementedException($"Key code {keyCode} is not implemented")
            };
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe SDLBool EventFilter(nint simulatorAddress, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type != SDL_EventType.WindowDestroyed)
            {
                SDL_Window sdlWindow = SDL3.SDL3.SDL_GetWindowFromID(sdlEvent->window.windowID);
                if (sdlWindow.Value != default)
                {
                    Simulator simulator = new(simulatorAddress);
                    ref GlobalKeyboardAndMouseSystem system = ref simulator.GetSystem<GlobalKeyboardAndMouseSystem>().Value;
                    SDL_DisplayID displayId = SDL3.SDL3.SDL_GetDisplayForWindow(sdlWindow);
                    SDL_DisplayMode* displayMode = SDL3.SDL3.SDL_GetCurrentDisplayMode(displayId);
                    system.screenWidth = (uint)displayMode->w;
                    system.screenHeight = (uint)displayMode->h;
                }
            }

            return true;
        }
    }
}