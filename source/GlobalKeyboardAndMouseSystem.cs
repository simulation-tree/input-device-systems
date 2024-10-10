using InputDevices.Components;
using InputDevices.Events;
using SDL3;
using SharpHook;
using SharpHook.Native;
using Simulation;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace InputDevices.Systems
{
    public class GlobalKeyboardAndMouseSystem : SystemBase
    {
        private readonly ComponentQuery<IsGlobal, IsKeyboard> globalKeyboardQuery;
        private readonly ComponentQuery<IsGlobal, IsMouse> globalMouseQuery;

        private TaskPoolGlobalHook? kbmHook;
        private KeyboardState currentKeyboard = default;
        private MouseState currentMouse = default;
        private KeyboardState lastKeyboard = default;
        private MouseState lastMouse = default;
        private bool mouseMoved;
        private bool mouseScrolled;
        private Vector2 mousePosition;
        private Vector2 mouseScroll;
        private uint globalKeyboardEntity;
        private uint globalMouseEntity;
        private uint screenWidth;
        private uint screenHeight;
        private unsafe readonly delegate* unmanaged<nint, SDL_Event*, SDL_bool> eventFilterFunction;

        public unsafe GlobalKeyboardAndMouseSystem(World world) : base(world)
        {
            globalKeyboardQuery = new();
            globalMouseQuery = new();
            Subscribe<InputUpdate>(Update);

            eventFilterFunction = &EventFilter;
            SDL3.SDL3.SDL_AddEventWatch(eventFilterFunction, world.Address);
        }

        public override void Dispose()
        {
            if (kbmHook is not null)
            {
                kbmHook.Dispose();
            }

            globalMouseQuery.Dispose();
            globalKeyboardQuery.Dispose();
            base.Dispose();
        }

        private void Update(InputUpdate update)
        {
            FindGlobalDevices();
            UpdateStates();
        }

        private void FindGlobalDevices()
        {
            globalKeyboardEntity = default;
            globalMouseEntity = default;
            globalKeyboardQuery.Update(world);
            foreach (var r in globalKeyboardQuery)
            {
                if (globalKeyboardEntity == default)
                {
                    globalKeyboardEntity = r.entity;
                }
                else
                {
                    throw new InvalidOperationException("Multiple global keyboards detected");
                }
            }

            globalMouseQuery.Update(world);
            foreach (var r in globalMouseQuery)
            {
                if (globalMouseEntity == default)
                {
                    globalMouseEntity = r.entity;
                }
                else
                {
                    throw new InvalidOperationException("Multiple global mice detected");
                }
            }

            if (kbmHook is null)
            {
                if (globalKeyboardEntity != default || globalMouseEntity != default)
                {
                    kbmHook = InitializeGlobalHook();
                }
            }
        }

        private TaskPoolGlobalHook InitializeGlobalHook()
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

        private void UpdateStates()
        {
            if (globalKeyboardEntity != default)
            {
                Keyboard keyboard = new(world, globalKeyboardEntity);
                bool keyboardUpdated = false;
                for (uint i = 0; i < KeyboardState.MaxKeyCount; i++)
                {
                    bool next = currentKeyboard[i];
                    bool previous = lastKeyboard[i];
                    ButtonState state = keyboard.GetButtonState(i);
                    ButtonState current = new(previous, next);
                    if (state != current)
                    {
                        lastKeyboard[i] = next;
                        keyboard.SetButtonState(i, current);
                        keyboardUpdated = true;
                    }
                }

                if (keyboardUpdated)
                {
                    DateTimeOffset when = DateTimeOffset.Now;
                    TimeSpan timestamp = when - DateTimeOffset.UnixEpoch;
                    keyboard.device.SetUpdateTime(timestamp);
                }
            }

            if (globalMouseEntity != default)
            {
                Mouse mouse = new(world, globalMouseEntity);
                bool mouseUpdated = false;
                for (uint i = 0; i < MouseState.MaxButtonCount; i++)
                {
                    bool next = currentMouse[i];
                    bool previous = lastMouse[i];
                    ButtonState state = mouse.GetButtonState(i);
                    ButtonState current = new(previous, next);
                    if (state != current)
                    {
                        lastMouse[i] = next;
                        mouse.SetButtonState(i, current);
                        mouseUpdated = true;
                    }
                }

                if (mouseMoved)
                {
                    mouse.Position = mousePosition;
                }

                if (mouseScrolled)
                {
                    mouse.Scroll = mouseScroll;
                }

                if (mouseUpdated || mouseMoved || mouseScrolled)
                {
                    DateTimeOffset when = DateTimeOffset.Now;
                    TimeSpan timestamp = when - DateTimeOffset.UnixEpoch;
                    mouse.device.SetUpdateTime(timestamp);
                    mouseMoved = false;
                    mouseScrolled = false;
                }
            }
        }

        private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
        {
            if (world != default && e.Data.KeyCode != KeyCode.VcUndefined)
            {
                Keyboard.Button control = GetControl(e.Data.KeyCode);
                if (!currentKeyboard[(uint)control])
                {
                    currentKeyboard[(uint)control] = true;
                }
            }
        }

        private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            if (world != default && e.Data.KeyCode != KeyCode.VcUndefined)
            {
                Keyboard.Button control = GetControl(e.Data.KeyCode);
                if (currentKeyboard[(uint)control])
                {
                    currentKeyboard[(uint)control] = false;
                }
            }
        }

        private void OnMousePressed(object? sender, MouseHookEventArgs e)
        {
            if (world != default)
            {
                uint control = (uint)e.Data.Button;
                if (!currentMouse[control])
                {
                    currentMouse[control] = true;
                }
            }
        }

        private void OnMouseReleased(object? sender, MouseHookEventArgs e)
        {
            if (world != default)
            {
                uint control = (uint)e.Data.Button;
                if (currentMouse[control])
                {
                    currentMouse[control] = false;
                }
            }
        }

        private void OnMouseDragged(object? sender, MouseHookEventArgs e)
        {
            if (world != default)
            {
                mousePosition = new Vector2(e.Data.X, e.Data.Y);
                mouseMoved = true;
            }
        }

        private void OnMouseMoved(object? sender, MouseHookEventArgs e)
        {
            if (world != default)
            {
                mousePosition = new Vector2(e.Data.X, e.Data.Y);
                mouseMoved = true;
            }
        }

        private void OnMouseWheel(object? sender, MouseWheelHookEventArgs e)
        {
            if (world != default)
            {
                mouseScroll = new Vector2(e.Data.X, e.Data.Y);
                mouseScrolled = true;
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

        [UnmanagedCallersOnly]
        private static unsafe SDL_bool EventFilter(nint worldAddress, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type == SDL_EventType.WindowDestroyed)
            {
                return SDL_bool.SDL_TRUE;
            }

            SDL_Window sdlWindow = SDL3.SDL3.SDL_GetWindowFromID(sdlEvent->window.windowID);
            if (sdlWindow.Value != default)
            {
                World world = new(worldAddress);
                GlobalKeyboardAndMouseSystem system = Get<GlobalKeyboardAndMouseSystem>(world);
                SDL_DisplayID displayId = SDL3.SDL3.SDL_GetDisplayForWindow(sdlWindow);
                SDL_DisplayMode* displayMode = SDL3.SDL3.SDL_GetCurrentDisplayMode(displayId);
                system.screenWidth = (uint)displayMode->w;
                system.screenHeight = (uint)displayMode->h;
            }

            return SDL_bool.SDL_TRUE;
        }
    }
}