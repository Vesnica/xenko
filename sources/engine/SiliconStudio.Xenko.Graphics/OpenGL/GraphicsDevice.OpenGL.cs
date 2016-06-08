﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGL
#if SILICONSTUDIO_PLATFORM_ANDROID
extern alias opentkold;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenTK.Graphics;
using OpenTK.Platform;
using SiliconStudio.Core;
using SiliconStudio.Core.Diagnostics;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Xenko.Rendering;
using SiliconStudio.Xenko.Shaders;
using SiliconStudio.Xenko.Graphics.OpenGL;
using Color4 = SiliconStudio.Core.Mathematics.Color4;
#if SILICONSTUDIO_PLATFORM_ANDROID
using System.Text;
using System.Runtime.InteropServices;
using OpenTK.Platform.Android;
#elif SILICONSTUDIO_PLATFORM_IOS
using OpenTK.Platform.iPhoneOS;
#endif
#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
using OpenTK.Graphics.ES30;
using DrawBuffersEnum = OpenTK.Graphics.ES30.DrawBufferMode;
using PixelFormatGl = OpenTK.Graphics.ES30.PixelFormat;
using FramebufferAttachmentObjectType = OpenTK.Graphics.ES30.All;
#if !SILICONSTUDIO_PLATFORM_MONO_MOBILE
using BeginMode = OpenTK.Graphics.ES30.PrimitiveType;
#else
// Use GetProgramParameterName which is what needs to be used with the new version of OpenTK (but not yet ported on Xamarin)
using GetProgramParameterName = OpenTK.Graphics.ES30.ProgramParameter;
using FramebufferAttachment = OpenTK.Graphics.ES30.FramebufferSlot;
#endif
#else
using BeginMode = OpenTK.Graphics.OpenGL.PrimitiveType;
using OpenTK.Graphics.OpenGL;
#endif

#if SILICONSTUDIO_XENKO_UI_SDL
using WindowState = SiliconStudio.Xenko.Graphics.SDL.FormWindowState;
#else
using WindowState = OpenTK.WindowState;
#endif

namespace SiliconStudio.Xenko.Graphics
{
    /// <summary>
    /// Performs primitive-based rendering, creates resources, handles system-level variables, adjusts gamma ramp levels, and creates shaders.
    /// </summary>
    public partial class GraphicsDevice
    {
        private static readonly Logger Log = GlobalLogger.GetLogger("GraphicsDevice");

        internal int FrameCounter;

        // Used when locking asyncCreationLockObject
        private bool asyncCreationLockTaken;

        internal bool ApplicationPaused = false;
        internal bool ProfileEnabled = false;

        internal IWindowInfo deviceCreationWindowInfo;
        internal object asyncCreationLockObject = new object();
        internal OpenTK.Graphics.IGraphicsContext deviceCreationContext;

        internal int defaultVAO;

#if !SILICONSTUDIO_PLATFORM_MONO_MOBILE
        DebugProc debugCallbackInstance = DebugCallback;
#endif

        private const GraphicsPlatform GraphicPlatform =
#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
                                                            GraphicsPlatform.OpenGLES;
#else
                                                            GraphicsPlatform.OpenGL;
#endif

#if SILICONSTUDIO_PLATFORM_ANDROID
        // If context was set before Begin(), try to keep it after End()
        // (otherwise devices with no backbuffer flicker)
        private bool keepContextOnEnd;

        private IntPtr graphicsContextEglPtr;
        internal AndroidAsyncGraphicsContext androidAsyncDeviceCreationContext;
        internal bool AsyncPendingTaskWaiting; // Used when Workaround_Context_Tegra2_Tegra3

        // Workarounds for specific GPUs
        internal bool Workaround_Context_Tegra2_Tegra3;
#endif

        internal SamplerState DefaultSamplerState;
        internal DepthStencilState defaultDepthStencilState;
        internal BlendState defaultBlendState;
        internal GraphicsProfile requestedGraphicsProfile;
        internal int version; // queried version
        internal int currentVersion; // glGetVersion
        internal Texture WindowProvidedRenderTexture;

        internal bool HasVAO;

        internal bool HasDXT;

        internal bool HasDepthClamp;

#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
        internal bool HasDepth24;
        internal bool HasPackedDepthStencilExtension;
        internal bool HasExtTextureFormatBGRA8888;
        internal bool HasTextureFloat;
        internal bool HasTextureHalf;
        internal bool HasRenderTargetFloat;
        internal bool HasRenderTargetHalf;
        internal bool HasTextureRG;
#endif

        private int windowProvidedFrameBuffer;
        private bool isFramebufferSRGB;

        private int contextBeginCounter = 0;

        // TODO: Use some LRU scheme to clean up FBOs if not used frequently anymore.
        internal Dictionary<FBOKey, int> existingFBOs = new Dictionary<FBOKey,int>(); 

        private static GraphicsDevice _currentGraphicsDevice = null;

        [ThreadStatic] private static List<GraphicsDevice> _graphicsDevicesInUse;

        public static GraphicsDevice Current
        {
            get
            {
                if (_graphicsDevicesInUse != null && _graphicsDevicesInUse.Count > 0)
                    return _graphicsDevicesInUse[_graphicsDevicesInUse.Count - 1];

                return _currentGraphicsDevice;
            }

            set
            {
                _currentGraphicsDevice = value;
            }
        }

        private OpenTK.Graphics.IGraphicsContext graphicsContext;
        private OpenTK.Platform.IWindowInfo windowInfo;

#if SILICONSTUDIO_PLATFORM_WINDOWS_DESKTOP || SILICONSTUDIO_PLATFORM_LINUX
#if SILICONSTUDIO_XENKO_UI_SDL
        private SiliconStudio.Xenko.Graphics.SDL.Window gameWindow;
#else
        private OpenTK.GameWindow gameWindow;
#endif
#elif SILICONSTUDIO_PLATFORM_ANDROID
        private AndroidGameView gameWindow;
#elif SILICONSTUDIO_PLATFORM_IOS
        private iPhoneOSGameView gameWindow;
#endif

#if SILICONSTUDIO_PLATFORM_ANDROID
        [DllImport("libEGL.dll", EntryPoint = "eglGetCurrentContext")]
        internal static extern IntPtr EglGetCurrentContext();
#endif

#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
        public bool IsOpenGLES2 { get; private set; }

        // Need to change sampler state depending on if texture has mipmap or not during PreDraw
        private bool[] hasMipmaps = new bool[64];
#endif

        private int copyProgram = -1;
        private int copyProgramOffsetLocation = -1;
        private int copyProgramScaleLocation = -1;

        private int copyProgramSRgb = -1;
        private int copyProgramSRgbOffsetLocation = -1;
        private int copyProgramSRgbScaleLocation = -1;

        internal float[] SquareVertices = {
            0.0f, 0.0f,
            1.0f, 0.0f,
            0.0f, 1.0f, 
            1.0f, 1.0f,
        };

        internal Buffer SquareBuffer;
        internal CommandList MainCommandList; // temporary because of state changes done during UseOpenGLCreationContext

#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
#if SILICONSTUDIO_PLATFORM_MONO_MOBILE
        internal const TextureTarget TextureTargetTexture2D = TextureTarget.Texture2D;
        internal const TextureTarget3D TextureTargetTexture3D = TextureTarget3D.Texture3D;
#else
        internal const TextureTarget2d TextureTargetTexture2D = TextureTarget2d.Texture2D;
        internal const TextureTarget3d TextureTargetTexture3D = TextureTarget3d.Texture3D;
#endif
#else
        internal const TextureTarget TextureTargetTexture2D = TextureTarget.Texture2D;
        internal const TextureTarget TextureTargetTexture3D = TextureTarget.Texture3D;
#endif

        /// <summary>
        /// Gets the status of this device.
        /// </summary>
        public GraphicsDeviceStatus GraphicsDeviceStatus
        {
            get
            {
#if SILICONSTUDIO_PLATFORM_ANDROID
                if (graphicsContext != gameWindow.GraphicsContext)
                {
                    return GraphicsDeviceStatus.Reset;
                }
#endif

                // TODO implement GraphicsDeviceStatus for OpenGL
                return GraphicsDeviceStatus.Normal;
            }
        }

        public void Use()
        {
            if (_graphicsDevicesInUse == null)
                _graphicsDevicesInUse = new List<GraphicsDevice>();

            if (!_graphicsDevicesInUse.Contains(this))
                _graphicsDevicesInUse.Add(this);
        }

        public void Unuse()
        {
            if (_graphicsDevicesInUse == null)
                return;

            _graphicsDevicesInUse.Remove(this);

            if (_graphicsDevicesInUse.Count == 0)
                _graphicsDevicesInUse = null;
        }

        internal UseOpenGLCreationContext UseOpenGLCreationContext()
        {
            return new UseOpenGLCreationContext(this);
        }

        /// <summary>
        /// Marks context as active on the current thread.
        /// </summary>
        public void Begin()
        {
            ++contextBeginCounter;

#if SILICONSTUDIO_PLATFORM_ANDROID
            if (contextBeginCounter == 1)
            {
                if (Workaround_Context_Tegra2_Tegra3)
                {
                    Monitor.Enter(asyncCreationLockObject, ref asyncCreationLockTaken);
                }
                else
                {
                    // On first set, check if context was not already set before,
                    // in which case we won't unset it during End().
                    keepContextOnEnd = graphicsContextEglPtr == GraphicsDevice.EglGetCurrentContext();

                    if (keepContextOnEnd)
                    {
                        return;
                    }
                }
            }
#endif

            if (contextBeginCounter == 1)
            {
                graphicsContext.MakeCurrent(windowInfo);
            }
        }

        /// <summary>
        /// Unmarks context as active on the current thread.
        /// </summary>
        public void End()
        {
#if DEBUG
            EnsureContextActive();
#endif

            --contextBeginCounter;
            if (contextBeginCounter == 0)
            {
                //UnbindVertexArrayObject();

#if SILICONSTUDIO_PLATFORM_ANDROID
                if (Workaround_Context_Tegra2_Tegra3)
                {
                    graphicsContext.MakeCurrent(null);

                    // Notify that main context can be used from now on
                    if (asyncCreationLockTaken)
                    {
                        Monitor.Exit(asyncCreationLockObject);
                        asyncCreationLockTaken = false;
                    }
                }
                else if (!keepContextOnEnd)
                {
                    GraphicsDevice.UnbindGraphicsContext(graphicsContext);
                }
#else
                UnbindGraphicsContext(graphicsContext);
#endif
            }
            else if (contextBeginCounter < 0)
            {
                throw new Exception("End context was called more than Begin");
            }
        }

        internal Buffer GetSquareBuffer()
        {
            if (SquareBuffer == null)
            {
                SquareBuffer = Buffer.New(this, SquareVertices, BufferFlags.VertexBuffer);
            }

            return SquareBuffer;
        }

        internal int GetCopyProgram(bool srgb, out int offsetLocation, out int scaleLocation)
        {
            if (srgb)
            {
                if (copyProgramSRgb == -1)
                {
                    copyProgramSRgb = CreateCopyProgram(true, out copyProgramSRgbOffsetLocation, out copyProgramSRgbScaleLocation);
                }
                offsetLocation = copyProgramSRgbOffsetLocation;
                scaleLocation = copyProgramSRgbScaleLocation;
                return copyProgramSRgb;
            }
            else
            {
                if (copyProgram == -1)
                {
                    copyProgram = CreateCopyProgram(false, out copyProgramOffsetLocation, out copyProgramScaleLocation);
                }
                offsetLocation = copyProgramOffsetLocation;
                scaleLocation = copyProgramScaleLocation;
                return copyProgram;
            }
        }

        private int CreateCopyProgram(bool srgb, out int offsetLocation, out int scaleLocation)
        {
            const string copyVertexShaderSource =
                "attribute vec2 aPosition;   \n" +
                "varying vec2 vTexCoord;     \n" +
                "uniform vec4 uScale;     \n" +
                "uniform vec4 uOffset;     \n" +
                "void main()                 \n" +
                "{                           \n" +
                "   vec4 transformedPosition = aPosition.xyxy * uScale + uOffset;" +
                "   gl_Position = vec4(transformedPosition.zw * 2.0 - 1.0, 0.0, 1.0); \n" +
                "   vTexCoord = transformedPosition.xy;   \n" +
                "}                           \n";

            const string copyFragmentShaderSource =
                "precision mediump float;                            \n" +
                "varying vec2 vTexCoord;                             \n" +
                "uniform sampler2D s_texture;                        \n" +
                "void main()                                         \n" +
                "{                                                   \n" +
                "    gl_FragColor = texture2D(s_texture, vTexCoord); \n" +
                "}                                                   \n";

            const string copyFragmentShaderSourceSRgb =
                "precision mediump float;                            \n" +
                "varying vec2 vTexCoord;                             \n" +
                "uniform sampler2D s_texture;                        \n" +
                "void main()                                         \n" +
                "{                                                   \n" +
                "    vec4 color = texture2D(s_texture, vTexCoord);   \n" +
                "    gl_FragColor = vec4(sqrt(color.rgb), color.a); \n" +  // approximation of linear to SRgb
                "}                                                   \n";

            // First initialization of shader program
            int vertexShader = TryCompileShader(ShaderType.VertexShader, copyVertexShaderSource);
            int fragmentShader = TryCompileShader(ShaderType.FragmentShader, srgb ? copyFragmentShaderSourceSRgb : copyFragmentShaderSource);

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.BindAttribLocation(program, 0, "aPosition");
            GL.LinkProgram(program);

            int linkStatus;
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out linkStatus);

            if (linkStatus != 1)
                throw new InvalidOperationException("Error while linking GLSL shaders.");

            GL.UseProgram(program);
            var textureLocation = GL.GetUniformLocation(program, "s_texture");
            offsetLocation = GL.GetUniformLocation(program, "uOffset");
            scaleLocation = GL.GetUniformLocation(program, "uScale");
            GL.Uniform1(textureLocation, 0);

            return program;
        }

        public void EnableProfile(bool enabledFlag)
        {
            ProfileEnabled = true;
        }

        internal void EnsureContextActive()
        {
            // TODO: Better checks (is active context the expected one?)
#if SILICONSTUDIO_PLATFORM_ANDROID
            if (EglGetCurrentContext() == IntPtr.Zero)
                throw new InvalidOperationException("No OpenGL context bound.");
#else
            if (OpenTK.Graphics.GraphicsContext.CurrentContext == null)
                throw new InvalidOperationException("No OpenGL context bound.");
#endif
        }

        public void ExecuteCommandList(CommandList commandList)
        {
#if DEBUG
            EnsureContextActive();
#endif

            throw new NotImplementedException();
        }

        internal int FindOrCreateFBO(GraphicsResourceBase graphicsResource)
        {
            if (graphicsResource == WindowProvidedRenderTexture)
                return windowProvidedFrameBuffer;

            var texture = graphicsResource as Texture;
            if (texture != null)
            {
                return FindOrCreateFBO(texture);
            }

            throw new NotSupportedException();
        }

        internal int FindOrCreateFBO(Texture texture)
        {
            var isDepthBuffer = ((texture.Flags & TextureFlags.DepthStencil) != 0);
            lock (existingFBOs)
            {
                foreach (var key in existingFBOs)
                {
                    if ((isDepthBuffer && key.Key.DepthStencilBuffer == texture)
                        || !isDepthBuffer && key.Key.LastRenderTarget == 1 && key.Key.RenderTargets[0] == texture)
                        return key.Value;
                }
            }

            if (isDepthBuffer)
                return FindOrCreateFBO(texture, null);
            return FindOrCreateFBO(null, new[] { texture });
        }

        internal int FindOrCreateFBO(Texture depthStencilBuffer, Texture[] renderTargets)
        {
            int framebufferId;

            // Check for existing FBO matching this configuration
            lock (existingFBOs)
            {
                var fboKey = new FBOKey(depthStencilBuffer, renderTargets);

                // Is it the default provided render target?
                // TODO: Need to disable some part of rendering if either is null
                var isProvidedRenderTarget = (fboKey.LastRenderTarget == 1 && renderTargets[0] == WindowProvidedRenderTexture);
                if (isProvidedRenderTarget && depthStencilBuffer != null)
                {
                    throw new InvalidOperationException("It is impossible to bind device provided and user created buffers with OpenGL");
                }
                if (depthStencilBuffer == null && (isProvidedRenderTarget || fboKey.LastRenderTarget == 0)) // device provided framebuffer
                {
                    return windowProvidedFrameBuffer;
                }

                if (existingFBOs.TryGetValue(fboKey, out framebufferId))
                    return framebufferId;

                GL.GenFramebuffers(1, out framebufferId);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebufferId);
                int lastRenderTargetIndex = -1;
                if (renderTargets != null)
                {
                    for (int i = 0; i < renderTargets.Length; ++i)
                    {
                        if (renderTargets[i] != null)
                        {
                            lastRenderTargetIndex = i;
                            // TODO: enable color render buffers when Texture creates one for other types than depth/stencil.
                            //if (renderTargets[i].IsRenderbuffer)
                            //    GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, RenderbufferTarget.Renderbuffer, renderTargets[i].ResourceId);
                            //else
                                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTargetTexture2D, renderTargets[i].ResourceId, 0);
                        }
                    }
                }

#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
                if (!IsOpenGLES2)
#endif
                {
#if !SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
                    if (lastRenderTargetIndex <= 0)
                    {
                        GL.DrawBuffer(lastRenderTargetIndex != -1 ? DrawBufferMode.ColorAttachment0 : DrawBufferMode.None);
                    }
                    else
#endif
                    {
                        var drawBuffers = new DrawBuffersEnum[lastRenderTargetIndex + 1];
                        for (var i = 0; i <= lastRenderTargetIndex; ++i)
                            drawBuffers[i] = DrawBuffersEnum.ColorAttachment0 + i;
                        GL.DrawBuffers(lastRenderTargetIndex + 1, drawBuffers);
                    }
                }

                if (depthStencilBuffer != null)
                {
                    bool useSharedAttachment = depthStencilBuffer.ResourceIdStencil == depthStencilBuffer.ResourceId;
#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
                    if (IsOpenGLES2)  // FramebufferAttachment.DepthStencilAttachment is not supported in ES 2
                        useSharedAttachment = false;
#endif
                    var attachmentType = useSharedAttachment ? FramebufferAttachment.DepthStencilAttachment : FramebufferAttachment.DepthAttachment;

                    if (depthStencilBuffer.IsRenderbuffer)
                    {
                        // Bind depth-only or packed depth-stencil buffer
                        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, attachmentType, RenderbufferTarget.Renderbuffer, depthStencilBuffer.ResourceId);

                        // If stencil buffer is separate, it's resource id might be stored in depthStencilBuffer.Texture.ResouceIdStencil
                        if (depthStencilBuffer.HasStencil && !useSharedAttachment)
                            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.StencilAttachment, RenderbufferTarget.Renderbuffer, depthStencilBuffer.ResourceIdStencil);
                    }
                    else
                    {
                        // Bind depth-only or packed depth-stencil buffer
                        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachmentType, TextureTargetTexture2D, depthStencilBuffer.ResourceId, 0);

                        // If stencil buffer is separate, it's resource id might be stored in depthStencilBuffer.Texture.ResouceIdStencil
                        if (depthStencilBuffer.HasStencil && !useSharedAttachment)
                            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.StencilAttachment, TextureTargetTexture2D, depthStencilBuffer.ResourceIdStencil, 0);
                    }
                }

                var framebufferStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (framebufferStatus != FramebufferErrorCode.FramebufferComplete)
                {
                    throw new InvalidOperationException(string.Format("FBO is incomplete: RT {0} Depth {1} (error: {2})", renderTargets != null && renderTargets.Length > 0 ? renderTargets[0].ResourceId : 0, depthStencilBuffer != null ? depthStencilBuffer.ResourceId : 0, framebufferStatus));
                }

                existingFBOs.Add(new GraphicsDevice.FBOKey(depthStencilBuffer, renderTargets != null ? renderTargets.ToArray() : null), framebufferId);
            }

            return framebufferId;
        }

        private void InitializePostFeatures()
        {
            // Create and bind default VAO
            if (HasVAO)
            {
#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
                if (!IsOpenGLES2)
#endif
                {
                    GL.GenVertexArrays(1, out defaultVAO);
                    GL.BindVertexArray(defaultVAO);
                }
            }
        }

        internal int TryCompileShader(ShaderType shaderType, string sourceCode)
        {
            int shaderGL = GL.CreateShader(shaderType);
            GL.ShaderSource(shaderGL, sourceCode);
            GL.CompileShader(shaderGL);

            var log = GL.GetShaderInfoLog(shaderGL);

            int compileStatus;
            GL.GetShader(shaderGL, ShaderParameter.CompileStatus, out compileStatus);

            if (compileStatus != 1)
                throw new InvalidOperationException("Error while compiling GLSL shader: \n" + log);

            return shaderGL;
        }

        internal static void UnbindGraphicsContext(IGraphicsContext graphicsContext)
        {
            graphicsContext.MakeCurrent(null);

#if SILICONSTUDIO_PLATFORM_IOS
            // Seems like iPhoneOSGraphicsContext.MakeCurrent(null) doesn't remove current context
            // Let's do it manually
            OpenGLES.EAGLContext.SetCurrentContext(null);
#endif
        }

        private void OnApplicationPaused(object sender, EventArgs e)
        {
            // Block async resource creation
            Monitor.Enter(asyncCreationLockObject, ref asyncCreationLockTaken);

            ApplicationPaused = true;

            using (UseOpenGLCreationContext())
            {
                GL.Finish();
            }

            // Unset graphics context
            UnbindGraphicsContext(graphicsContext);
        }

        private void OnApplicationResumed(object sender, EventArgs e)
        {
            windowInfo = gameWindow.WindowInfo;

            // Reenable graphics context
            graphicsContext.MakeCurrent(windowInfo);

            ApplicationPaused = false;

            // Reenable async resource creation
            if (asyncCreationLockTaken)
            {
                Monitor.Exit(asyncCreationLockObject);
                asyncCreationLockTaken = false;
            }
        }

        private string renderer;

        private string GetRendererName()
        {
            return renderer;
        }

        protected void InitializePlatformDevice(GraphicsProfile[] graphicsProfiles, DeviceCreationFlags deviceCreationFlags, WindowHandle windowHandle)
        {
            // Enable OpenGL context sharing
            OpenTK.Graphics.GraphicsContext.ShareContexts = true;

            // TODO: How to control Debug flags?
            var creationFlags = GraphicsContextFlags.Default;

            if ((deviceCreationFlags & DeviceCreationFlags.Debug) != 0)
            {
                creationFlags |= GraphicsContextFlags.Debug;
                ProfileEnabled = true;
#if !SILICONSTUDIO_PLATFORM_MONO_MOBILE
                GL.DebugMessageCallback(debugCallbackInstance, IntPtr.Zero);
#endif
            }

            // set default values
            version = 100;

            requestedGraphicsProfile = GraphicsProfile.Level_9_1;

            // Find the first profile that is compatible with current GL version
            foreach (var graphicsProfile in graphicsProfiles)
            {
                if (Adapter.IsProfileSupported(graphicsProfile))
                {
                    requestedGraphicsProfile = graphicsProfile;
                    break;
                }
            }

            // Find back OpenGL version from requested version
            OpenGLUtils.GetGLVersion(requestedGraphicsProfile, out version);

            // check what is actually created
            if (!OpenGLUtils.GetCurrentGLVersion(out currentVersion))
            {
                currentVersion = version;
            }

#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLES
            IsOpenGLES2 = version < 300;
            creationFlags |= GraphicsContextFlags.Embedded;
#endif

            renderer = GL.GetString(StringName.Renderer);

#if SILICONSTUDIO_PLATFORM_LINUX || SILICONSTUDIO_PLATFORM_WINDOWS_DESKTOP
#if SILICONSTUDIO_XENKO_UI_SDL
            gameWindow = (SiliconStudio.Xenko.Graphics.SDL.Window)windowHandle.NativeHandle;
#else
            gameWindow = (OpenTK.GameWindow)windowHandle.NativeHandle;
#endif
#elif SILICONSTUDIO_PLATFORM_ANDROID
            gameWindow = (AndroidGameView)windowHandle.NativeHandle;
#elif SILICONSTUDIO_PLATFORM_IOS
            gameWindow = (iPhoneOSGameView)windowHandle.NativeHandle;
#endif

            windowInfo = gameWindow.WindowInfo;

            // Doesn't seems to be working on Android
#if SILICONSTUDIO_PLATFORM_ANDROID           
            // Force a reference to AndroidGameView from OpenTK 0.9, otherwise linking will fail in release mode for MonoDroid.
            typeof (opentkold::OpenTK.Platform.Android.AndroidGameView).ToString();
            graphicsContext = gameWindow.GraphicsContext;
            gameWindow.Load += OnApplicationResumed;
            gameWindow.Unload += OnApplicationPaused;
            
            Workaround_Context_Tegra2_Tegra3 = renderer == "NVIDIA Tegra 3" || renderer == "NVIDIA Tegra 2";

            var androidGraphicsContext = (AndroidGraphicsContext)graphicsContext;
            if (Workaround_Context_Tegra2_Tegra3)
            {
                // On Tegra2/Tegra3, we can't do any background context
                // As a result, we reuse main graphics context even when loading.
                // Of course, main graphics context need to be either available, or we register ourself for next ExecutePendingTasks().
                deviceCreationContext = graphicsContext;
                deviceCreationWindowInfo = windowInfo;

                // We don't want context to be set or it might collide with our internal use to create async resources
                // TODO: Reenabled, since the context seems to change otherwise. Do we need this in the first place, since we only want a single context?
                //gameWindow.AutoSetContextOnRenderFrame = false;
            }
            else
            {
                if (androidAsyncDeviceCreationContext != null)
                {
                    androidAsyncDeviceCreationContext.Dispose();
                    deviceCreationContext.Dispose();
                    deviceCreationWindowInfo.Dispose();
                }
                androidAsyncDeviceCreationContext = new AndroidAsyncGraphicsContext(androidGraphicsContext, (AndroidWindow)windowInfo, version / 100);
                deviceCreationContext = OpenTK.Graphics.GraphicsContext.CreateDummyContext(androidAsyncDeviceCreationContext.Context);
                deviceCreationWindowInfo = OpenTK.Platform.Utilities.CreateDummyWindowInfo();
            }

            graphicsContextEglPtr = EglGetCurrentContext();
#elif SILICONSTUDIO_PLATFORM_IOS
            graphicsContext = gameWindow.GraphicsContext;
            gameWindow.Load += OnApplicationResumed;
            gameWindow.Unload += OnApplicationPaused;

            var asyncContext = new OpenGLES.EAGLContext(IsOpenGLES2 ? OpenGLES.EAGLRenderingAPI.OpenGLES2 : OpenGLES.EAGLRenderingAPI.OpenGLES3, gameWindow.EAGLContext.ShareGroup);
            OpenGLES.EAGLContext.SetCurrentContext(asyncContext);
            deviceCreationContext = new OpenTK.Graphics.GraphicsContext(new OpenTK.ContextHandle(asyncContext.Handle), null, graphicsContext, version / 100, (version % 100) / 10, creationFlags);
            deviceCreationWindowInfo = windowInfo;
            gameWindow.MakeCurrent();
#else
#if SILICONSTUDIO_XENKO_UI_SDL
            // Because OpenTK really wants a Sdl2GraphicsContext and not a dummy one, we will create
            // a new one using the dummy one and invalidate the dummy one.
            graphicsContext = new OpenTK.Graphics.GraphicsContext(gameWindow.DummyGLContext.GraphicsMode, windowInfo, version / 100, (version % 100) / 10, creationFlags);
            gameWindow.DummyGLContext.Dispose();
#else
            graphicsContext = gameWindow.Context;
#endif
            deviceCreationWindowInfo = windowInfo;
            deviceCreationContext = new OpenTK.Graphics.GraphicsContext(graphicsContext.GraphicsMode, deviceCreationWindowInfo, version / 100, (version % 100) / 10, creationFlags);

            OpenTK.Graphics.GraphicsContext.CurrentContext.MakeCurrent(null);
#endif

            // Restore main context
            graphicsContext.MakeCurrent(windowInfo);

#if !SILICONSTUDIO_PLATFORM_MONO_MOBILE
            // Setup GL debug log callback
            if ((deviceCreationFlags & DeviceCreationFlags.Debug) != 0)
            {
                GL.DebugMessageCallback(debugCallbackInstance, IntPtr.Zero);
            }
#endif

            // Create default OpenGL State objects
            DefaultSamplerState = SamplerState.New(this, new SamplerStateDescription(TextureFilter.MinPointMagMipLinear, TextureAddressMode.Wrap) { MaxAnisotropy = 1 }).DisposeBy(this);
        }

        private void AdjustDefaultPipelineStateDescription(ref PipelineStateDescription pipelineStateDescription)
        {
        }

#if !SILICONSTUDIO_PLATFORM_MONO_MOBILE
        private static void DebugCallback(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userparam)
        {
            if (severity == DebugSeverity.DebugSeverityHigh)
            {
                string msg = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message);
                Log.Error("[GL] {0}; {1}; {2}; {3}; {4}", source, type, id, severity, msg);
            }
        }
#endif

        protected void DestroyPlatformDevice()
        {
            // Hack: Reset the lock so that UseOpenGLCreationContext works (even if locked by previously called OnApplicationPaused, which might have been done in an unaccessible event thread)
            // TODO: Does it work with Tegra3?
            if (ApplicationPaused)
            {
                asyncCreationLockObject = new object();
            }

#if SILICONSTUDIO_PLATFORM_ANDROID || SILICONSTUDIO_PLATFORM_IOS
            gameWindow.Load -= OnApplicationResumed;
            gameWindow.Unload -= OnApplicationPaused;
#endif
        }

        internal void OnDestroyed()
        {
            // Clear existing FBOs
            lock (existingFBOs)
            {
                existingFBOs.Clear();
                existingFBOs[new FBOKey(null, new[] { WindowProvidedRenderTexture })] = windowProvidedFrameBuffer;
            }

            //// Clear bound states
            //for (int i = 0; i < boundTextures.Length; ++i)
                //boundTextures[i] = null;

            //boundFrontFace = FrontFaceDirection.Ccw;

            //boundVertexArrayObject = null;
            //enabledVertexAttribArrays = 0;
            //boundDepthStencilState = null;
            //boundStencilReference = 0;
            //boundBlendState = null;
            //boundRasterizerState = null;
            //boundDepthStencilBuffer = null;

            //for (int i = 0; i < boundRenderTargets.Length; ++i)
                //boundRenderTargets[i] = null;

            //boundFBO = 0;
            //boundFBOHeight = 0;
            //boundProgram = 0;
        }

        internal void InitDefaultRenderTarget(PresentationParameters presentationParameters)
        {
#if DEBUG
            EnsureContextActive();
#endif

            // TODO: Provide unified ClientSize from GameWindow
#if SILICONSTUDIO_PLATFORM_IOS
            windowProvidedFrameBuffer = gameWindow.Framebuffer;

            // Scale for Retina display
            var width = (int)(gameWindow.Size.Width * gameWindow.ContentScaleFactor);
            var height = (int)(gameWindow.Size.Height * gameWindow.ContentScaleFactor);
#else
#if SILICONSTUDIO_XENKO_GRAPHICS_API_OPENGLCORE
            var width = gameWindow.ClientSize.Width;
            var height = gameWindow.ClientSize.Height;
#else
            var width = gameWindow.Size.Width;
            var height = gameWindow.Size.Height;
#endif
            windowProvidedFrameBuffer = 0;
#endif

            // TODO OPENGL detect if created framebuffer is sRGB or not (note: improperly reported by FramebufferParameterName.FramebufferAttachmentColorEncoding)
            isFramebufferSRGB = true;

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, windowProvidedFrameBuffer);

            // TODO: iOS (and possibly other platforms): get real render buffer ID for color/depth?
            WindowProvidedRenderTexture = Texture.New2D(this, width, height, 1,
                // TODO: As a workaround, because OpenTK(+OpenGLES) doesn't support to create SRgb backbuffer, we fake it by creating a non-SRgb here and CopyScaler2D is responsible to transform it to non SRgb
                isFramebufferSRGB ? presentationParameters.BackBufferFormat : presentationParameters.BackBufferFormat.ToNonSRgb(), TextureFlags.RenderTarget | Texture.TextureFlagsCustomResourceId);
            WindowProvidedRenderTexture.Reload = graphicsResource => { };

            // Extract FBO render target
            if (windowProvidedFrameBuffer != 0)
            {
                int framebufferAttachmentType;
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentObjectType, out framebufferAttachmentType);
                if (framebufferAttachmentType == (int)FramebufferAttachmentObjectType.Texture)
                {
                    int renderTargetTextureId;
                    GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentObjectName, out renderTargetTextureId);
                    WindowProvidedRenderTexture.resourceId = renderTargetTextureId;
                }
            }

            existingFBOs[new FBOKey(null, new[] { WindowProvidedRenderTexture })] = windowProvidedFrameBuffer;
        }

        private class SwapChainBackend
        {
            /// <summary>
            /// Default constructor to initialize fields that are not explicitly set to avoid warnings at compile time.
            /// </summary>
            internal SwapChainBackend()
            {
                PresentationParameters = null;
                PresentCount = 0;
            }

            public PresentationParameters PresentationParameters;
            public int PresentCount;
        }

        /// <summary>
        /// Creates a swap chain from presentation parameters.
        /// </summary>
        /// <param name="presentationParameters">The presentation parameters.</param>
        /// <returns></returns>
        private SwapChainBackend CreateSwapChainBackend(PresentationParameters presentationParameters)
        {
            var swapChainBackend = new SwapChainBackend();
            return swapChainBackend;
        }

        /// <summary>
        /// Gets the default presentation parameters associated with this graphics device.
        /// </summary>
        public PresentationParameters PresentationParameters
        {
            get
            {
                throw new InvalidOperationException(FrameworkResources.NoDefaultRenterTarget);
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether this GraphicsDevice is in fullscreen.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if this GraphicsDevice is fullscreen; otherwise, <c>false</c>.
        /// </value>
        public bool IsFullScreen
        {
            get
            {
#if SILICONSTUDIO_PLATFORM_WINDOWS_DESKTOP || SILICONSTUDIO_PLATFORM_LINUX
                return gameWindow.WindowState == WindowState.Fullscreen;
#else
                throw new NotImplementedException();
#endif
            }

            set
            {
#if SILICONSTUDIO_PLATFORM_WINDOWS_DESKTOP || SILICONSTUDIO_PLATFORM_LINUX
                if (value ^ (gameWindow.WindowState == WindowState.Fullscreen))
                    gameWindow.WindowState = value ? WindowState.Fullscreen : WindowState.Normal;
#else
                throw new NotImplementedException();
#endif
            }
        }

#if SILICONSTUDIO_PLATFORM_ANDROID
        // Execute pending asynchronous object creation
        // (on android devices where we can't create background context such as Tegra2/Tegra3)
        internal void ExecutePendingTasks()
        {
            // Unbind context
            graphicsContext.MakeCurrent(null);

            // Release and reacquire lock
            Monitor.Wait(asyncCreationLockObject);

            // Rebind context
            graphicsContext.MakeCurrent(windowInfo);
        }
#endif

        internal struct FBOKey : IEquatable<FBOKey>
        {
            public readonly Texture DepthStencilBuffer;
            public readonly Texture[] RenderTargets;
            public readonly int LastRenderTarget;

            public FBOKey(Texture depthStencilBuffer, Texture[] renderTargets)
            {
                DepthStencilBuffer = depthStencilBuffer;

                LastRenderTarget = 0;
                if (renderTargets != null)
                {
                    for (int i = 0; i < renderTargets.Length; ++i)
                    {
                        if (renderTargets[i] != null)
                        {
                            LastRenderTarget = i + 1;
                            break;
                        }
                    }
                }

                RenderTargets = LastRenderTarget != 0 ? renderTargets : null;
            }

            public bool Equals(FBOKey obj2)
            {
                if (obj2.DepthStencilBuffer != DepthStencilBuffer) return false;

                // Should have same number of render targets
                if (LastRenderTarget != obj2.LastRenderTarget)
                    return false;

                // Since both object have same LastRenderTarget, array is valid at least until this spot.
                for (int i = 0; i < LastRenderTarget; ++i)
                    if (obj2.RenderTargets[i] != RenderTargets[i])
                        return false;

                return true;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is FBOKey)) return false;

                var obj2 = (FBOKey)obj;

                return Equals(obj2);
            }

            public override int GetHashCode()
            {
                var result = DepthStencilBuffer != null ? DepthStencilBuffer.GetHashCode() : 0;
                if (RenderTargets != null)
                {
                    for (int index = 0; index < LastRenderTarget; index++)
                    {
                        var renderTarget = RenderTargets[index];
                        result ^= renderTarget != null ? renderTarget.GetHashCode() : 0;
                    }
                }
                return result;
            }
        }
    }
}

#endif
