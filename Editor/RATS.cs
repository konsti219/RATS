// RATS - Raz's Animator Tweaks'n Stuff
// Original AnimatorExtensions by Dj Lukis.LT, under MIT License

// Copyright (c) 2023 Razgriz
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using ReorderableList = UnityEditorInternal.ReorderableList;
using HarmonyLib;

namespace Razgriz.RATS
{
    [InitializeOnLoad]
    public partial class RATS
    {
        public static Harmony harmonyInstance = new Harmony("Razgriz.RATS");
        private static int wait = 0;
        public static RATSPreferences Prefs = new RATSPreferences();

        static RATS()
        {
            Debug.Log("RATS v" + RATSGUI.version);
            RATSGUI.HandlePreferences();
            // Register our patch delegate
            EditorApplication.update += DoPatches;
            HandleTextures();
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        static void DoPatches()
        {
            // Wait a couple cycles to patch to let static initializers run
            wait++;
            if(wait > 2)
            {
                HandleTextures();
                harmonyInstance.PatchAll();
                // Unregister our delegate so it doesn't run again
                EditorApplication.update -= DoPatches;
                Debug.Log("[Rats] Running Patches");
            }
        }

        [InitializeOnLoadMethod]
        private static void OnProjectLoadedInEditor()
        {
            HandleTextures();
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void DidReloadScripts()
        {
            HandleTextures();
        }

        private static void PlayModeChanged(PlayModeStateChange state)
        {
            HandleTextures();
        }
        
        #region Helpers

        private struct InternalTextureInfo
        {
            public int Width;
            public int Height;
            public bool Mips;
            public bool Linear;
            public TextureFormat Format;
            public IntPtr Ptr;

            public InternalTextureInfo(int width, int height, TextureFormat format, bool mips, bool linear, IntPtr ptr)
            {
                Width = width; Height = height; Mips = mips; Linear = linear; Format = format; Ptr = ptr;
            }

            public InternalTextureInfo(Texture2D tex, bool linear)
            {
                Width = tex.width; Height = tex.height; Mips = tex.mipmapCount > 0; Linear = linear; Format = tex.format; Ptr = tex.GetNativeTexturePtr();
            }

            public Texture2D GetTexture2D() => Texture2D.CreateExternalTexture(Width, Height, Format, Mips, Linear, Ptr);
        }

        // Recursive helper functions to gather deeply-nested parameter references
        private static void GatherBtParams(BlendTree bt, ref Dictionary<string, AnimatorControllerParameter> srcParams, ref Dictionary<string, AnimatorControllerParameter> queuedParams)
        {
            if (srcParams.ContainsKey(bt.blendParameter))
                queuedParams[bt.blendParameter] = srcParams[bt.blendParameter];
            if (srcParams.ContainsKey(bt.blendParameterY))
                queuedParams[bt.blendParameterY] = srcParams[bt.blendParameterY];

            foreach (var cmotion in bt.children)
            {
                if (srcParams.ContainsKey(cmotion.directBlendParameter))
                    queuedParams[cmotion.directBlendParameter] = srcParams[cmotion.directBlendParameter];

                // Go deeper to nested BlendTrees
                var cbt = cmotion.motion as BlendTree;
                if (!(cbt is null))
                    GatherBtParams(cbt, ref srcParams, ref queuedParams);
            }
        }
        
        private static void GatherSmParams(AnimatorStateMachine sm, ref Dictionary<string, AnimatorControllerParameter> srcParams, ref Dictionary<string, AnimatorControllerParameter> queuedParams)
        {
            // Go over states to check controlling or BlendTree params
            foreach (var cstate in sm.states)
            {
                var s = cstate.state;
                if (s.mirrorParameterActive && srcParams.ContainsKey(s.mirrorParameter))
                    queuedParams[s.mirrorParameter] = srcParams[s.mirrorParameter];
                if (s.speedParameterActive && srcParams.ContainsKey(s.speedParameter))
                    queuedParams[s.speedParameter] = srcParams[s.speedParameter];
                if (s.timeParameterActive && srcParams.ContainsKey(s.timeParameter))
                    queuedParams[s.timeParameter] = srcParams[s.timeParameter];
                if (s.cycleOffsetParameterActive && srcParams.ContainsKey(s.cycleOffsetParameter))
                    queuedParams[s.cycleOffsetParameter] = srcParams[s.cycleOffsetParameter];

                var bt = s.motion as BlendTree;
                if (!(bt is null))
                    GatherBtParams(bt, ref srcParams, ref queuedParams);
            }

            // Go over all transitions
            var transitions = new List<AnimatorStateTransition>(sm.anyStateTransitions.Length);
            transitions.AddRange(sm.anyStateTransitions);
            foreach (var cstate in sm.states)
                transitions.AddRange(cstate.state.transitions);
            foreach (var transition in transitions)
            foreach (var cond in transition.conditions)
                if (srcParams.ContainsKey(cond.parameter))
                    queuedParams[cond.parameter] = srcParams[cond.parameter];

            // Go deeper to child sate machines
            foreach (var csm in sm.stateMachines)
                GatherSmParams(csm.stateMachine, ref srcParams, ref queuedParams);
        }
        
        // Layer Copy/Paste Functions
        private static AnimatorControllerLayer _layerClipboard = null;
        private static AnimatorController _controllerClipboard = null;

        private static void CopyLayer(object layerControllerView)
        {
            var rlist = (ReorderableList)LayerListField.GetValue(layerControllerView);
            var ctrl = Traverse.Create(layerControllerView).Field("m_Host").Property("animatorController").GetValue<AnimatorController>();
            _layerClipboard = rlist.list[rlist.index] as AnimatorControllerLayer;
            _controllerClipboard = ctrl;
            Unsupported.CopyStateMachineDataToPasteboard(_layerClipboard.stateMachine, ctrl, rlist.index);
        }

        public static void PasteLayer(object layerControllerView)
        {
            if (_layerClipboard == null)
                return;
            var rlist = (ReorderableList)LayerListField.GetValue(layerControllerView);
            var ctrl = Traverse.Create(layerControllerView).Field("m_Host").Property("animatorController").GetValue<AnimatorController>();

            // Will paste layer right below selected one
            int targetindex = rlist.index + 1;
            string newname = ctrl.MakeUniqueLayerName(_layerClipboard.name);
            Undo.FlushUndoRecordObjects();

            // Use unity built-in function to clone state machine
            ctrl.AddLayer(newname);
            var layers = ctrl.layers;
            int pastedlayerindex = layers.Length - 1;
            var pastedlayer = layers[pastedlayerindex];
            Unsupported.PasteToStateMachineFromPasteboard(pastedlayer.stateMachine, ctrl, pastedlayerindex, Vector3.zero);

            // Promote from child to main
            var pastedsm = pastedlayer.stateMachine.stateMachines[0].stateMachine;
            pastedsm.name = newname;
            pastedlayer.stateMachine.stateMachines = new ChildAnimatorStateMachine[0];
            UnityEngine.Object.DestroyImmediate(pastedlayer.stateMachine, true);
            pastedlayer.stateMachine = pastedsm;
            PasteLayerProperties(pastedlayer, _layerClipboard);

            // Move up to desired spot
            for (int i = layers.Length-1; i > targetindex; i--)
                layers[i] = layers[i - 1];
            layers[targetindex] = pastedlayer;
            ctrl.layers = layers;

            // Make layer unaffected by undo, forces user to delete manually but prevents dangling sub-assets
            Undo.ClearUndo(ctrl);

            // Pasting to different controller, sync parameters
            if (ctrl != _controllerClipboard)
            {
                Undo.IncrementCurrentGroup();
                int curgroup = Undo.GetCurrentGroup();
                Undo.RecordObject(ctrl, "Sync pasted layer parameters");

                // cache names
                // TODO: do this before pasting to workaround default values not being copied
                var destparams = new Dictionary<string, AnimatorControllerParameter>(ctrl.parameters.Length);
                foreach (var param in ctrl.parameters)
                    destparams[param.name] = param;

                var srcparams = new Dictionary<string, AnimatorControllerParameter>(_controllerClipboard.parameters.Length);
                foreach (var param in _controllerClipboard.parameters)
                    srcparams[param.name] = param;

                var queuedparams = new Dictionary<string, AnimatorControllerParameter>(_controllerClipboard.parameters.Length);

                // Recursively loop over all nested state machines
                GatherSmParams(pastedsm, ref srcparams, ref queuedparams);

                // Sync up whats missing
                foreach (var param in queuedparams.Values)
                {
                    string pname = param.name;
                    if (!destparams.ContainsKey(pname))
                    {
                        Debug.Log("Transferring parameter "+pname); // TODO: count or concatenate names?
                        ctrl.AddParameter(param);
                        // note: queuedparams should not have duplicates so don't need to append to destparams
                    }
                }
                Undo.CollapseUndoOperations(curgroup);
            }

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Update list selection
            Traverse.Create(layerControllerView).Property("selectedLayerIndex").SetValue(targetindex);
        }

        public static void PasteLayerSettings(object layerControllerView)
        {
            var rlist = (ReorderableList)LayerListField.GetValue(layerControllerView);
            AnimatorController ctrl = Traverse.Create(layerControllerView).Field("m_Host").Property("animatorController").GetValue<AnimatorController>();

            var layers = ctrl.layers;
            var targetlayer = layers[rlist.index];
            PasteLayerProperties(targetlayer, _layerClipboard);
            ctrl.layers = layers; // needed for edits to apply
        }

        public static void PasteLayerProperties(AnimatorControllerLayer dest, AnimatorControllerLayer src)
        {
            dest.avatarMask = src.avatarMask;
            dest.blendingMode = src.blendingMode;
            dest.defaultWeight = src.defaultWeight;
            dest.iKPass = src.iKPass;
            dest.syncedLayerAffectsTiming = src.syncedLayerAffectsTiming;
            dest.syncedLayerIndex = src.syncedLayerIndex;
        }

        #endregion Helpers

        #region ReflectionCache
        // Animator Window
        private static readonly Type AnimatorWindowType = AccessTools.TypeByName("UnityEditor.Graphs.AnimatorControllerTool");
        private static readonly MethodInfo AnimatorControllerGetter = AccessTools.PropertyGetter(AnimatorWindowType, "animatorController");

        private static readonly Type AnimatorWindowGraphGUIType = AccessTools.TypeByName("UnityEditor.Graphs.GraphGUI");
        private static readonly FieldInfo AnimatorWindowGraphGridColorMajor = AccessTools.Field(AnimatorWindowGraphGUIType, "gridMajorColor");
        private static readonly FieldInfo AnimatorWindowGraphGridColorMinor = AccessTools.Field(AnimatorWindowGraphGUIType, "gridMinorColor");
        private static readonly Type AnimatorWindowStylesType = AccessTools.TypeByName("UnityEditor.Graphs.Styles");
        private static readonly FieldInfo AnimatorWindowGraphStyleBackground = AccessTools.Field(AnimatorWindowStylesType, "graphBackground");

        private static readonly FieldInfo AnimatorWindowGraphGraph = AccessTools.Field(AnimatorWindowGraphGUIType, "m_Graph");

        private static readonly Type GraphStylesType = AccessTools.TypeByName("UnityEditor.Graphs.Styles");

        private static readonly Type AnimatorControllerType = AccessTools.TypeByName("UnityEditor.Animations.AnimatorController");
        private static readonly Type AnimatorStateMachineType = AccessTools.TypeByName("UnityEditor.Animations.AnimatorStateMachine");
        private static readonly Type AnimatorStateType = AccessTools.TypeByName("UnityEditor.Animations.AnimatorState");
        private static readonly Type ParameterControllerViewType = AccessTools.TypeByName("UnityEditor.Graphs.ParameterControllerView");

        private static readonly Type LayerControllerViewType = AccessTools.TypeByName("UnityEditor.Graphs.LayerControllerView");
        private static readonly FieldInfo LayerScrollField = AccessTools.Field(LayerControllerViewType, "m_LayerScroll");
        private static readonly FieldInfo LayerListField = AccessTools.Field(LayerControllerViewType, "m_LayerList");

        private static readonly Type RenameOverlayType = AccessTools.TypeByName("UnityEditor.RenameOverlay");
        private static readonly MethodInfo BeginRenameMethod = AccessTools.Method(RenameOverlayType, "BeginRename");

        private static readonly Type AnimatorTransitionInspectorBaseType = AccessTools.TypeByName("UnityEditor.Graphs.AnimationStateMachine.AnimatorTransitionInspectorBase");
        private static readonly MethodInfo GetElementHeightMethod = AccessTools.Method(typeof(ReorderableList), "GetElementHeight", new Type[]{typeof(int)});
        private static readonly MethodInfo GetElementYOffsetMethod = AccessTools.Method(typeof(ReorderableList), "GetElementYOffset", new Type[]{typeof(int)});
        
        private static GUIStyle StateMotionStyle = null;
        private static GUIStyle StateExtrasStyle = null;
        private static GUIStyle StateExtrasStyleActive = null;
        private static GUIStyle StateExtrasStyleInactive = null;
        private static GUIStyle StateBlendtreeStyle = null;
        private static bool _refocusSelectedLayer = false;

        // Animation Window
        static readonly Assembly EditorAssembly = typeof(Editor).Assembly;
        static readonly Type AnimationWindowHierarchyGUIType = EditorAssembly.GetType("UnityEditorInternal.AnimationWindowHierarchyGUI");
        static readonly Type AnimationWindowHierarchyNodeType = EditorAssembly.GetType("UnityEditorInternal.AnimationWindowHierarchyNode");
        static readonly Type AnimationWindowUtilityType = EditorAssembly.GetType("UnityEditorInternal.AnimationWindowUtility");

        static readonly Type AnimEditorType = AccessTools.TypeByName("UnityEditor.AnimEditor");

        static readonly FieldInfo NodeTypePropertyName = AnimationWindowHierarchyNodeType.GetField("propertyName", BindingFlags.Instance | BindingFlags.Public);
        static readonly FieldInfo NodeTypePath = AnimationWindowHierarchyNodeType.GetField("path", BindingFlags.Instance | BindingFlags.Public);
        static readonly FieldInfo NodeTypeAnimatableObjectType = AnimationWindowHierarchyNodeType.GetField("animatableObjectType", BindingFlags.Instance | BindingFlags.Public);
        static readonly FieldInfo NodeTypeIndent = AnimationWindowHierarchyNodeType.GetField("indent", BindingFlags.Instance | BindingFlags.Public);

        static readonly PropertyInfo NodeDisplayNameProp = AnimationWindowHierarchyNodeType.GetProperty("displayName", BindingFlags.Instance | BindingFlags.Public);

        #endregion ReflectionCache

        #region TextureHandling

        private static Texture2D nodeBackgroundImageMask;
        private static Color[] nodeBackgroundPixels;
        private static Color[] nodeBackgroundActivePixels;
        private static Color[] stateMachineBackgroundPixels;
        private static Color[] stateMachineBackgroundPixelsActive;

        private static Texture2D nodeBackgroundImage;
        private static Texture2D nodeBackgroundImageActive;
        private static Texture2D nodeBackgroundImageBlue;
        private static Texture2D nodeBackgroundImageBlueActive;
        private static Texture2D nodeBackgroundImageAqua;
        private static Texture2D nodeBackgroundImageAquaActive;
        private static Texture2D nodeBackgroundImageGreen;
        private static Texture2D nodeBackgroundImageGreenActive;
        private static Texture2D nodeBackgroundImageYellow;
        private static Texture2D nodeBackgroundImageYellowActive;
        private static Texture2D nodeBackgroundImageOrange;
        private static Texture2D nodeBackgroundImageOrangeActive;
        private static Texture2D nodeBackgroundImageRed;
        private static Texture2D nodeBackgroundImageRedActive;

        private static Texture2D stateMachineBackgroundImage;
        private static Texture2D stateMachineBackgroundImageActive;

        // TODO: This texture handling code feels pretty inefficient but it only runs when adjusting so I'm not too concerned
        static void HandleTextures()
        {
            byte[] nodeBackgroundBytes = System.IO.File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), AssetDatabase.GUIDToAssetPath("780a9e3efb8a1ca42b44c98c5e972f2d")).Replace("/", "\\"));
            byte[] nodeBackgroundActiveBytes = System.IO.File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), AssetDatabase.GUIDToAssetPath("4fb6ef4881973e24cbcf73cff14ae0c8")).Replace("/", "\\"));
            nodeBackgroundImageMask = LoadPNG(Path.Combine(Directory.GetCurrentDirectory(), AssetDatabase.GUIDToAssetPath("81dcb3a363364ea4f9a475b4cebb0eaf")).Replace("/", "\\"));
            
            stateMachineBackgroundImage = LoadPNG(Path.Combine(Directory.GetCurrentDirectory(), AssetDatabase.GUIDToAssetPath("160541e301c89e644a9c10fb82f74f88")).Replace("/", "\\"));
            stateMachineBackgroundPixels = stateMachineBackgroundImage.GetPixels();
            stateMachineBackgroundImageActive = LoadPNG(Path.Combine(Directory.GetCurrentDirectory(), AssetDatabase.GUIDToAssetPath("c430ad55db449494aa1caefe9dccdc2d")).Replace("/", "\\"));
            stateMachineBackgroundPixelsActive = stateMachineBackgroundImageActive.GetPixels();

            nodeBackgroundPixels = LoadPNG(nodeBackgroundBytes).GetPixels();
            nodeBackgroundActivePixels = LoadPNG(nodeBackgroundActiveBytes).GetPixels();

            nodeBackgroundImage = LoadPNG(nodeBackgroundBytes); 
            nodeBackgroundImageBlue = LoadPNG(nodeBackgroundBytes);
            nodeBackgroundImageAqua = LoadPNG(nodeBackgroundBytes);
            nodeBackgroundImageGreen = LoadPNG(nodeBackgroundBytes);
            nodeBackgroundImageYellow = LoadPNG(nodeBackgroundBytes);
            nodeBackgroundImageOrange = LoadPNG(nodeBackgroundBytes);
            nodeBackgroundImageRed = LoadPNG(nodeBackgroundBytes);

            nodeBackgroundImageActive = LoadPNG(nodeBackgroundActiveBytes);
            nodeBackgroundImageBlueActive = LoadPNG(nodeBackgroundActiveBytes);
            nodeBackgroundImageAquaActive = LoadPNG(nodeBackgroundActiveBytes);
            nodeBackgroundImageGreenActive = LoadPNG(nodeBackgroundActiveBytes);
            nodeBackgroundImageYellowActive = LoadPNG(nodeBackgroundActiveBytes);
            nodeBackgroundImageOrangeActive = LoadPNG(nodeBackgroundActiveBytes);
            nodeBackgroundImageRedActive = LoadPNG(nodeBackgroundActiveBytes);

            // These aren't really used as far as I can tell, so no user customization needed
            TintTexture2D(ref nodeBackgroundImageBlue, nodeBackgroundImageMask, new Color(27/255f, 27/255f, 150/255f, 1f));
            TintTexture2D(ref nodeBackgroundImageYellow, nodeBackgroundImageMask, new Color(204/255f, 165/255f, 39/255f, 1f));

            UpdateGraphTextures();
        }

        public static void UpdateGraphTextures()
        {
            try
            {
                Color glowTint = new Color(44/255f, 119/255f, 212/255f, 1f);
                Texture2D glowState = new Texture2D(nodeBackgroundImageActive.width, nodeBackgroundImageActive.height);
                Texture2D glowStateMachine = new Texture2D(stateMachineBackgroundImageActive.width, stateMachineBackgroundImageActive.height);
                glowState.SetPixels(nodeBackgroundActivePixels);
                glowStateMachine.SetPixels(stateMachineBackgroundPixelsActive);
                TintTexture2D(ref glowState, glowTint);
                TintTexture2D(ref glowStateMachine, glowTint);
                Color[] glowData = glowState.GetPixels();
                Color[] glowStateMachineData = glowStateMachine.GetPixels();

                stateMachineBackgroundImage.SetPixels(stateMachineBackgroundPixels);

                nodeBackgroundImageBlue.SetPixels(nodeBackgroundPixels);
                nodeBackgroundImageYellow.SetPixels(nodeBackgroundPixels);
                nodeBackgroundImage.SetPixels(nodeBackgroundPixels);
                nodeBackgroundImageAqua.SetPixels(nodeBackgroundPixels);
                nodeBackgroundImageGreen.SetPixels(nodeBackgroundPixels);
                nodeBackgroundImageOrange.SetPixels(nodeBackgroundPixels);
                nodeBackgroundImageRed.SetPixels(nodeBackgroundPixels);

                stateMachineBackgroundImageActive.SetPixels(glowStateMachineData);

                nodeBackgroundImageActive.SetPixels(glowData);
                nodeBackgroundImageBlueActive.SetPixels(glowData);
                nodeBackgroundImageYellowActive.SetPixels(glowData);
                nodeBackgroundImageAquaActive.SetPixels(glowData);
                nodeBackgroundImageGreenActive.SetPixels(glowData);
                nodeBackgroundImageOrangeActive.SetPixels(glowData);
                nodeBackgroundImageRedActive.SetPixels(glowData);

                // Main color tint
                TintTexture2D(ref stateMachineBackgroundImage, RATS.Prefs.StateColorGray);
                TintTexture2D(ref nodeBackgroundImage, RATS.Prefs.StateColorGray);
                TintTexture2D(ref nodeBackgroundImageAqua, RATS.Prefs.StateColorAqua);
                TintTexture2D(ref nodeBackgroundImageGreen, RATS.Prefs.StateColorGreen);
                TintTexture2D(ref nodeBackgroundImageOrange, RATS.Prefs.StateColorOrange);
                TintTexture2D(ref nodeBackgroundImageRed, RATS.Prefs.StateColorRed); 

                // Glowing edge for selected
                AddTexture2D(ref stateMachineBackgroundImageActive, stateMachineBackgroundImage);
                AddTexture2D(ref nodeBackgroundImageActive, nodeBackgroundImage);
                AddTexture2D(ref nodeBackgroundImageBlueActive, nodeBackgroundImageBlue);
                AddTexture2D(ref nodeBackgroundImageAquaActive, nodeBackgroundImageAqua);
                AddTexture2D(ref nodeBackgroundImageGreenActive, nodeBackgroundImageGreen);
                AddTexture2D(ref nodeBackgroundImageYellowActive, nodeBackgroundImageYellow);
                AddTexture2D(ref nodeBackgroundImageOrangeActive, nodeBackgroundImageOrange);
                AddTexture2D(ref nodeBackgroundImageRedActive, nodeBackgroundImageRed);
            }
            catch(MissingReferenceException e)
            {
                Debug.Log("Texture Update Exception Caught: " + e.ToString());
            }
        }

        private static byte[] GetFileBytes(string filePath)
        {
            byte[] fileData = System.IO.File.ReadAllBytes(filePath);
            return fileData;
        }

        public static Texture2D LoadPNGFromGUID(string guid)
        {
            return LoadPNG(Path.Combine(Directory.GetCurrentDirectory(), AssetDatabase.GUIDToAssetPath(guid)).Replace("/", "\\"));
        }

        public static Texture2D LoadPNG(string filePath)
        {
            Texture2D tex = null;
            byte[] fileData;

            if (System.IO.File.Exists(filePath))
            {
                fileData = System.IO.File.ReadAllBytes(filePath);
                tex = new Texture2D(2, 2);
                tex.LoadImage(fileData); //..this will auto-resize the texture dimensions.
            }
            return tex;
        }

        public static string FindFile(string name, string type=null)
        {
            string[] guids;
            if (type != null)
                guids = AssetDatabase.FindAssets(name + " t:" + type);
            else
                guids = AssetDatabase.FindAssets(name);
            if (guids.Length == 0)
                return null;
            return AssetDatabase.GUIDToAssetPath(guids[0]);
        }

        public static Texture2D LoadPNG(byte[] fileData)
        {
            Texture2D tex = null;
            tex = new Texture2D(2, 2);
            tex.LoadImage(fileData); //..this will auto-resize the texture dimensions.
            return tex;
        }

        private static void TintTexture2D(ref Texture2D texture, Color tint, bool recalculateMips = true, bool makeNoLongerReadable = false)
        {
            Color[] pixels = texture.GetPixels();
            Parallel.For(0, pixels.Length, (j, state) => { pixels[j] *= tint; });

            texture.SetPixels(pixels);
            texture.Apply(recalculateMips, makeNoLongerReadable);
        } 

        private static void TintTexture2D(ref Texture2D texture, Texture2D maskTexture, Color tint, bool recalculateMips = true, bool makeNoLongerReadable = false)
        {
            Color[] pixels = texture.GetPixels();
            Color[] mask = maskTexture.GetPixels();
            Parallel.For(0, pixels.Length, (j, state) => { pixels[j] *= Color.Lerp(Color.white, tint, mask[j].r); });

            texture.SetPixels(pixels);
            texture.Apply(recalculateMips, makeNoLongerReadable);
        }

        private static void AddTexture2D(ref Texture2D texture, Texture2D textureToAdd, bool recalculateMips = true, bool makeNoLongerReadable = false)
        {
            Color[] pixels = texture.GetPixels();
            Color[] pixelsToAdd = textureToAdd.GetPixels();
            Parallel.For(0, pixels.Length, (j, state) => { pixels[j] += pixelsToAdd[j];});

            texture.SetPixels(pixels); 
            texture.Apply(recalculateMips, makeNoLongerReadable);
        }

        #endregion TextureHandling
    }
}
#endif
