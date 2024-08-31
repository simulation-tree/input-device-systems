using InputDevices.Components;
using InputDevices.Events;
using SDL3;
using Simulation;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unmanaged.Collections;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    public class WindowDevicesSystems : SystemBase
    {
        private static readonly Dictionary<nint, WindowDevicesSystems> systems = new();

        private readonly UnmanagedDictionary<uint, uint> keyboardEntities;
        private readonly UnmanagedDictionary<uint, KeyboardState> currentKeyboards;
        private readonly UnmanagedDictionary<uint, KeyboardState> lastKeyboards;
        private readonly UnmanagedDictionary<uint, uint> mouseEntities;
        private readonly UnmanagedDictionary<uint, MouseState> currentMice;
        private readonly UnmanagedDictionary<uint, MouseState> lastMice;
        private unsafe readonly delegate* unmanaged<nint, SDL_Event*, int> eventFilterFunction;

        public unsafe WindowDevicesSystems(World world) : base(world)
        {
            keyboardEntities = new();
            currentKeyboards = new();
            lastKeyboards = new();
            mouseEntities = new();
            currentMice = new();
            lastMice = new();
            systems.Add(world.Address, this);
            Subscribe<InputUpdate>(Update);

            eventFilterFunction = &EventFilter;
            SDL_AddEventWatch(eventFilterFunction, world.Address);
        }

        public unsafe override void Dispose()
        {
            SDL_DelEventWatch(eventFilterFunction, world.Address);
            mouseEntities.Dispose();
            currentMice.Dispose();
            lastMice.Dispose();
            keyboardEntities.Dispose();
            currentKeyboards.Dispose();
            lastKeyboards.Dispose();
            base.Dispose();
        }

        private void Update(InputUpdate update)
        {
            UpdateEntitiesToMatchDevices();
            AdvancePreviousStates();
        }

        private void UpdateEntitiesToMatchDevices()
        {
            foreach (uint keyboardId in currentKeyboards.Keys)
            {
                Keyboard keyboard = GetOrCreateKeyboard(keyboardId);
                Entity entity = keyboard;
                ref KeyboardState state = ref entity.GetComponentRef<IsKeyboard>().state;
                ref KeyboardState lastState = ref entity.GetComponentRef<LastKeyboardState>().value;
                state = currentKeyboards[keyboardId];
                lastState = lastKeyboards[keyboardId];
            }

            foreach (uint mouseId in currentMice.Keys)
            {
                Mouse mouse = GetOrCreateMouse(mouseId);
                Entity entity = mouse;
                ref MouseState state = ref entity.GetComponentRef<IsMouse>().state;
                ref MouseState lastState = ref entity.GetComponentRef<LastMouseState>().value;
                state = currentMice[mouseId];
                lastState = lastMice[mouseId];
            }
        }

        private void AdvancePreviousStates()
        {
            foreach (uint keyboardId in currentKeyboards.Keys)
            {
                ref KeyboardState lastState = ref lastKeyboards[keyboardId];
                lastState = currentKeyboards[keyboardId];
            }

            foreach (uint mouseId in currentMice.Keys)
            {
                ref MouseState lastState = ref lastMice[mouseId];
                lastState = currentMice[mouseId];
            }
        }

        private void KeyboardEvent(SDL_EventType type, SDL_KeyboardDeviceEvent kdevice, SDL_KeyboardEvent key)
        {
            uint keyboardId = (uint)kdevice.which;
            if (type == SDL_EventType.KeyboardRemoved)
            {
                if (currentKeyboards.TryRemove(keyboardId, out _))
                {
                    lastKeyboards.Remove(keyboardId);
                }
            }
            else
            {
                if (!currentKeyboards.ContainsKey(keyboardId))
                {
                    currentKeyboards.Add(keyboardId, new());
                    lastKeyboards.Add(keyboardId, new());
                }

                uint control = (uint)key.scancode;
                ref KeyboardState currentState = ref currentKeyboards[keyboardId];
                if (type == SDL_EventType.KeyDown)
                {
                    currentState[control] = true;
                }
                else if (type == SDL_EventType.KeyUp)
                {
                    currentState[control] = false;
                }
            }
        }

        private void MouseEvent(SDL_EventType type, SDL_MouseDeviceEvent mdevice, SDL_MouseMotionEvent motion, SDL_MouseWheelEvent wheel, SDL_MouseButtonEvent button, SDL_WindowID windowId)
        {
            uint mouseId = (uint)mdevice.which;
            if (type == SDL_EventType.MouseRemoved)
            {
                if (currentMice.TryRemove(mouseId, out _))
                {
                    lastMice.Remove(mouseId);
                }
            }
            else
            {
                if (!currentMice.ContainsKey(mouseId))
                {
                    currentMice.Add(mouseId, new());
                    lastMice.Add(mouseId, new());
                }

                ref MouseState currentState = ref currentMice[mouseId];
                if (type == SDL_EventType.MouseMotion)
                {
                    SDL_Window window = SDL_GetWindowFromID(windowId);
                    SDL_GetWindowSize(window, out _, out int height);
                    currentState.positionX = (int)motion.x;
                    currentState.positionY = height - (int)motion.y;
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    currentState.scrollX = (int)wheel.x;
                    currentState.scrollY = (int)wheel.y;
                }
                else if (type == SDL_EventType.MouseButtonDown)
                {
                    uint control = button.button;
                    currentState[control] = true;
                }
                else if (type == SDL_EventType.MouseButtonUp)
                {
                    uint control = button.button;
                    currentState[control] = false;
                }
            }
        }

        private Keyboard GetOrCreateKeyboard(uint keyboardId)
        {
            if (!keyboardEntities.TryGetValue(keyboardId, out uint keyboardEntity))
            {
                keyboardEntity = world.CreateEntity();
                world.AddComponent(keyboardEntity, new IsKeyboard());
                world.AddComponent(keyboardEntity, new LastKeyboardState());
                keyboardEntities.Add(keyboardId, keyboardEntity);
            }

            return new Keyboard(world, keyboardEntity);
        }

        private Mouse GetOrCreateMouse(uint mouseId)
        {
            if (!mouseEntities.TryGetValue(mouseId, out uint mouseEntity))
            {
                mouseEntity = world.CreateEntity();
                world.AddComponent(mouseEntity, new IsMouse());
                world.AddComponent(mouseEntity, new LastMouseState());
                mouseEntities.Add(mouseId, mouseEntity);
            }

            return new Mouse(world, mouseEntity);
        }

        [UnmanagedCallersOnly]
        private static unsafe int EventFilter(nint worldAddress, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type == SDL_EventType.MouseMotion || type == SDL_EventType.MouseWheel || type == SDL_EventType.MouseAdded ||
                type == SDL_EventType.MouseRemoved || type == SDL_EventType.MouseButtonDown || type == SDL_EventType.MouseButtonUp)
            {
                WindowDevicesSystems system = systems[worldAddress];
                system.MouseEvent(type, sdlEvent->mdevice, sdlEvent->motion, sdlEvent->wheel, sdlEvent->button, sdlEvent->window.windowID);
            }
            else if (type == SDL_EventType.KeyboardAdded || type == SDL_EventType.KeyboardRemoved || type == SDL_EventType.KeyDown || type == SDL_EventType.KeyUp)
            {
                WindowDevicesSystems system = systems[worldAddress];
                system.KeyboardEvent(type, sdlEvent->kdevice, sdlEvent->key);
            }

            return 1;
        }
    }
}