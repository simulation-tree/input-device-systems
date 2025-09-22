using Collections.Generic;
using InputDevices.Components;
using InputDevices.Messages;
using Rendering.Components;
using SDL3;
using Simulation;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unmanaged;
using Windows.Components;
using Worlds;
using static SDL3.SDL3;

namespace InputDevices.Systems
{
    [SkipLocalsInit]
    public partial class WindowDevicesSystems : SystemBase, IListener<InputUpdate>
    {
        private readonly World world;
        private readonly Dictionary<uint, uint> windows;
        private readonly Array<Vector2> windowSizes;
        private readonly Array<Device<IsKeyboard>> keyboards;
        private readonly Array<Device<IsMouse>> mice;
        private readonly GCHandle handle;
        private unsafe delegate* unmanaged[Cdecl]<nint, SDL_Event*, SDLBool> eventFilterFunction;
        private readonly int keyboardType;
        private readonly int mouseType;
        private readonly int timestampType;
        private readonly int windowType;
        private readonly int destinationType;
        private readonly BitMask windowComponentTypes;

        public WindowDevicesSystems(Simulator simulator, World world) : base(simulator)
        {
            handle = GCHandle.Alloc(this, GCHandleType.Normal);
            this.world = world;
            windows = new(8);
            windowSizes = new(8);
            keyboards = new(8);
            mice = new(8);

            Schema schema = world.Schema;
            keyboardType = schema.GetComponentType<IsKeyboard>();
            mouseType = schema.GetComponentType<IsMouse>();
            timestampType = schema.GetComponentType<LastDeviceUpdateTime>();
            windowType = schema.GetComponentType<IsWindow>();
            destinationType = schema.GetComponentType<IsDestination>();
            windowComponentTypes = new(windowType, destinationType);
            AddListener();
        }

        public override void Dispose()
        {
            RemoveListener();

            foreach (Device<IsKeyboard> keyboard in keyboards)
            {
                if (keyboard.deviceEntity != default)
                {
                    world.DestroyEntity(keyboard.deviceEntity);
                }
            }

            foreach (Device<IsMouse> mouse in mice)
            {
                if (mouse.deviceEntity != default)
                {
                    world.DestroyEntity(mouse.deviceEntity);
                }
            }

            mice.Dispose();
            keyboards.Dispose();
            windowSizes.Dispose();
            windows.Dispose();
            handle.Free();
        }

        private unsafe void AddListener()
        {
            eventFilterFunction = &EventFilter;
            SDL_AddEventWatch(eventFilterFunction, GCHandle.ToIntPtr(handle));
        }

        private unsafe void RemoveListener()
        {
            SDL_RemoveEventWatch(eventFilterFunction, GCHandle.ToIntPtr(handle));
        }

        void IListener<InputUpdate>.Receive(ref InputUpdate message)
        {
            FindWindows();
            UpdateEntities();
        }

        private void FindWindows()
        {
            //remove windows that no longer exist
            Span<uint> toRemove = stackalloc uint[windows.Count];
            int removeCount = 0;
            foreach ((uint windowId, uint windowEntity) in windows)
            {
                if (!world.ContainsEntity(windowEntity))
                {
                    toRemove[removeCount++] = windowId;
                }
            }

            for (int i = 0; i < removeCount; i++)
            {
                windows.Remove(toRemove[i]);
            }

            //add windows and gather their sizes
            Span<Vector2> windowSizesSpan = windowSizes.AsSpan();
            ReadOnlySpan<Chunk> chunks = world.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.ComponentTypes.ContainsAll(windowComponentTypes))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsWindow> windowComponents = chunk.GetComponents<IsWindow>(windowType);
                    ComponentEnumerator<IsDestination> destinationComponents = chunk.GetComponents<IsDestination>(destinationType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsWindow component = ref windowComponents[i];
                        ref IsDestination destination = ref destinationComponents[i];
                        if (!windows.ContainsKey(component.id))
                        {
                            windows.Add(component.id, entities[i]);
                            int capacity = (int)(component.id + 1).GetNextPowerOf2();
                            if (windowSizesSpan.Length < capacity)
                            {
                                windowSizes.Length = capacity;
                                windowSizesSpan = windowSizes.AsSpan();
                            }
                        }

                        windowSizesSpan[(int)component.id] = new Vector2(destination.width, destination.height);
                    }
                }
            }
        }

        private void UpdateEntities()
        {
            Span<Device<IsKeyboard>> keyboardsSpan = keyboards.AsSpan();
            Span<Vector2> windowSizesSpan = windowSizes.AsSpan();
            Span<Device<IsMouse>> miceSpan = mice.AsSpan();
            for (int keyboardId = 0; keyboardId < keyboardsSpan.Length; keyboardId++)
            {
                ref Device<IsKeyboard> keyboard = ref keyboardsSpan[keyboardId];
                if (keyboard.real)
                {
                    if (keyboard.deviceEntity == default)
                    {
                        Keyboard newKeyboard = new(world, (rint)1);
                        newKeyboard.AddReference(keyboard.windowEntity);
                        keyboard.deviceEntity = newKeyboard.value;
                    }

                    ref IsKeyboard component = ref world.GetComponent<IsKeyboard>(keyboard.deviceEntity, keyboardType);
                    component.currentState = keyboard.component.currentState;
                    component.lastState = keyboard.component.lastState;
                    keyboard.component.lastState = keyboard.component.currentState;
                }
            }

            for (int mouseId = 0; mouseId < miceSpan.Length; mouseId++)
            {
                ref Device<IsMouse> mouse = ref miceSpan[mouseId];
                if (mouse.real)
                {
                    if (mouse.deviceEntity == default)
                    {
                        Mouse newMouse = new(world, (rint)1);
                        newMouse.AddReference(mouse.windowEntity);
                        mouse.deviceEntity = newMouse.value;
                    }

                    ref IsMouse component = ref world.GetComponent<IsMouse>(mouse.deviceEntity, mouseType);
                    mouse.component.currentState.cursor = component.currentState.cursor;
                    if (component.currentState.cursor != component.lastState.cursor)
                    {
                        SDL_Cursor currentCursor = SDL_GetCursor();
                        SDL_DestroyCursor(currentCursor);

                        if (component.currentState.cursor == Mouse.Cursor.Default)
                        {
                            SDL_Cursor defaultCursor = SDL_GetDefaultCursor();
                            SDL_SetCursor(defaultCursor);
                        }
                        else
                        {
                            SDL_Cursor customCursor = SDL_CreateSystemCursor((SDL_SystemCursor)component.currentState.cursor);
                            SDL_SetCursor(customCursor);
                        }
                    }

                    Vector2 windowSize = windowSizesSpan[(int)mouse.windowId];
                    component.currentState = mouse.component.currentState;
                    component.currentState.position.Y = windowSize.Y - component.currentState.position.Y; //invert
                    component.lastState = mouse.component.lastState;
                    mouse.component.lastState = mouse.component.currentState;
                    mouse.component.currentState.scroll = default;
                    mouse.component.currentState.delta = default;
                }
            }
        }

        private void KeyboardEvent(uint windowId, SDL_EventType type, SDL_KeyboardDeviceEvent kdevice, SDL_KeyboardEvent key)
        {
            int keyboardId = (int)kdevice.which;
            if (type == SDL_EventType.KeyboardRemoved)
            {
                ref Device<IsKeyboard> keyboard = ref keyboards[keyboardId];
                if (keyboard.real)
                {
                    world.DestroyEntity(keyboard.deviceEntity);
                    keyboard = default;
                }
            }
            else
            {
                bool keyDown = type == SDL_EventType.KeyDown;
                if (keyDown || type == SDL_EventType.KeyUp)
                {
                    Span<Device<IsKeyboard>> keyboardsSpan = keyboards.AsSpan();
                    if (keyboardsSpan.Length <= keyboardId)
                    {
                        keyboards.Length = keyboardId + 1;
                        keyboardsSpan = keyboards.AsSpan();
                    }

                    ref Device<IsKeyboard> keyboard = ref keyboardsSpan[keyboardId];
                    if (!keyboard.real)
                    {
                        if (windows.TryGetValue(windowId, out uint windowEntity))
                        {
                            keyboard = new(windowEntity, windowId);
                        }
                        else
                        {
                            //window doesnt exist yet? could happen if input system runs before a window system does
                            return;
                        }
                    }

                    if (keyDown)
                    {
                        keyboard.component.currentState[(int)key.scancode] = true;
                    }
                    else
                    {
                        keyboard.component.currentState[(int)key.scancode] = false;
                    }
                }
            }
        }

        private void MouseEvent(uint windowId, SDL_EventType type, SDL_MouseDeviceEvent mdevice, SDL_MouseMotionEvent motion, SDL_MouseWheelEvent wheel, SDL_MouseButtonEvent button)
        {
            int mouseId = (int)mdevice.which;
            if (type == SDL_EventType.MouseRemoved)
            {
                ref Device<IsMouse> mouse = ref mice[mouseId];
                if (mouse.real)
                {
                    world.DestroyEntity(mouse.deviceEntity);
                    mouse = default;
                }
            }
            else
            {
                if (type == SDL_EventType.MouseMotion)
                {
                    Span<Device<IsMouse>> miceSpan = mice.AsSpan();
                    if (miceSpan.Length <= mouseId)
                    {
                        mice.Length = mouseId + 1;
                        miceSpan = mice.AsSpan();
                    }

                    ref Device<IsMouse> mouse = ref miceSpan[mouseId];
                    if (!mouse.real)
                    {
                        if (windows.TryGetValue(windowId, out uint windowEntity))
                        {
                            mouse = new(windowEntity, windowId);
                        }
                        else
                        {
                            //window doesnt exist yet? could happen if input system runs before a window system does
                            return;
                        }
                    }

                    mouse.component.currentState.position = new(motion.x, motion.y);
                    mouse.component.currentState.delta += new Vector2(motion.xrel, -motion.yrel); //invert Y axis
                }
                else if (type == SDL_EventType.MouseWheel)
                {
                    Span<Device<IsMouse>> miceSpan = mice.AsSpan();
                    if (miceSpan.Length <= mouseId)
                    {
                        mice.Length = mouseId + 1;
                        miceSpan = mice.AsSpan();
                    }

                    ref Device<IsMouse> mouse = ref miceSpan[mouseId];
                    if (!mouse.real)
                    {
                        if (windows.TryGetValue(windowId, out uint windowEntity))
                        {
                            mouse = new(windowId, windowId);
                        }
                        else
                        {
                            //window doesnt exist yet? could happen if input system runs before a window system does
                            return;
                        }
                    }

                    mouse.component.currentState.scroll += new Vector2(wheel.x, wheel.y);
                }
                else if (type == SDL_EventType.MouseButtonDown)
                {
                    Span<Device<IsMouse>> miceSpan = mice.AsSpan();
                    if (miceSpan.Length <= mouseId)
                    {
                        mice.Length = mouseId + 1;
                        miceSpan = mice.AsSpan();
                    }

                    ref Device<IsMouse> mouse = ref miceSpan[mouseId];
                    if (!mouse.real)
                    {
                        if (windows.TryGetValue(windowId, out uint windowEntity))
                        {
                            mouse = new(windowId, windowId);
                        }
                        else
                        {
                            //window doesnt exist yet? could happen if input system runs before a window system does
                            return;
                        }
                    }

                    mouse.component.currentState[button.button] = true;
                }
                else if (type == SDL_EventType.MouseButtonUp)
                {
                    Span<Device<IsMouse>> miceSpan = mice.AsSpan();
                    if (miceSpan.Length <= mouseId)
                    {
                        mice.Length = mouseId + 1;
                        miceSpan = mice.AsSpan();
                    }

                    ref Device<IsMouse> mouse = ref miceSpan[mouseId];
                    if (!mouse.real)
                    {
                        if (windows.TryGetValue(windowId, out uint windowEntity))
                        {
                            mouse = new(windowId, windowId);
                        }
                        else
                        {
                            //window doesnt exist yet? could happen if input system runs before a window system does
                            return;
                        }
                    }

                    mouse.component.currentState[button.button] = false;
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe SDLBool EventFilter(nint handle, SDL_Event* sdlEvent)
        {
            SDL_EventType type = sdlEvent->type;
            if (type is SDL_EventType.MouseMotion or SDL_EventType.MouseWheel || type == SDL_EventType.MouseAdded ||
                type == SDL_EventType.MouseRemoved || type == SDL_EventType.MouseButtonDown || type == SDL_EventType.MouseButtonUp)
            {
                WindowDevicesSystems system = GetSystem(handle);
                uint windowId = (uint)sdlEvent->window.windowID;
                system.MouseEvent(windowId, type, sdlEvent->mdevice, sdlEvent->motion, sdlEvent->wheel, sdlEvent->button);
            }
            else if (type == SDL_EventType.KeyboardAdded || type == SDL_EventType.KeyboardRemoved || type == SDL_EventType.KeyDown || type == SDL_EventType.KeyUp)
            {
                WindowDevicesSystems system = GetSystem(handle);
                uint windowId = (uint)sdlEvent->window.windowID;
                system.KeyboardEvent(windowId, type, sdlEvent->kdevice, sdlEvent->key);
            }

            return true;
        }

        private static WindowDevicesSystems GetSystem(nint handle)
        {
            ThrowIfHandleIsFree(handle);

            return (WindowDevicesSystems)GCHandle.FromIntPtr(handle).Target!;
        }

        [Conditional("DEBUG")]
        private static void ThrowIfHandleIsFree(nint handle)
        {
            GCHandle gcHandle = GCHandle.FromIntPtr(handle);
            if (!gcHandle.IsAllocated)
            {
                throw new InvalidOperationException("The handle is not allocated. It may have been freed or not initialized properly");
            }

            if (gcHandle.Target is not WindowDevicesSystems)
            {
                throw new InvalidOperationException("The handle does not point to a valid WindowDevicesSystems instance");
            }
        }
    }
}