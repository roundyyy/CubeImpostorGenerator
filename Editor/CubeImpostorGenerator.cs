using UnityEngine;
using UnityEditor;
using System.IO;
using System;
using System.Linq;
using System.Collections.Generic;

public class CubeImpostorGenerator : EditorWindow
{
    GameObject sourceObject;
    int textureSize = 512;
    private readonly int[] textureSizeOptions = { 64, 128, 256, 512, 1024, 2048 };
    private int selectedTextureSizeIndex = 3; // Default to 512 (index 3 in the array)
    float trimAmount = 0f;
    Shader selectedShader;
    bool createLOD = true;
    bool applyToPrefab = false; // New field for Apply to Prefab

    bool markAsStatic = false;

    private static List<Shader> availableShaders = new List<Shader>();
    private static string[] shaderNames;
    private int selectedShaderIndex = 0;

    private const int BakingLayer = 4;

    private Dictionary<GameObject, int> originalLayers = new Dictionary<GameObject, int>();

    // Field to hold status messages
    private string statusMessage = "";

    [MenuItem("Tools/Roundy/Cube Impostor Generator")]
    public static void ShowWindow()
    {
        GetWindow<CubeImpostorGenerator>("Cube Impostor Generator v0.1");
    }

    private void OnEnable()
    {
        availableShaders = Resources.FindObjectsOfTypeAll<Shader>().ToList();
        shaderNames = availableShaders.Select(shader => shader.name).ToArray();
        int standardIndex = availableShaders.FindIndex(s => s.name == "Standard");
        selectedShaderIndex = standardIndex >= 0 ? standardIndex : 0;
        selectedShader = availableShaders[selectedShaderIndex];
    }

    void OnGUI()
    {
        GUILayout.Label("Cube Impostor Generator v0.1", EditorStyles.boldLabel);

        sourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Source Object", "The GameObject to create an impostor for."),
            sourceObject, typeof(GameObject), true);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(new GUIContent("Atlas Texture Size", "The size of the final atlas texture. Higher values result in better quality but larger file size."));
        selectedTextureSizeIndex = EditorGUILayout.Popup(selectedTextureSizeIndex, Array.ConvertAll(textureSizeOptions, x => x.ToString()));
        EditorGUILayout.EndHorizontal();

        trimAmount = EditorGUILayout.Slider(
            new GUIContent("Trim Amount", "Adjusts the trimming of empty space around the impostor. Positive values increase trimming, negative values add padding."),
            trimAmount, -0.05f, 0.05f);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(new GUIContent("Shader", "The shader to use for the impostor material. A simple shader like Diffuse is recommended for better performance."));
        selectedShaderIndex = EditorGUILayout.Popup(selectedShaderIndex, shaderNames);
        EditorGUILayout.EndHorizontal();

        if (selectedShaderIndex >= 0 && selectedShaderIndex < availableShaders.Count)
        {
            selectedShader = availableShaders[selectedShaderIndex];
        }

        createLOD = EditorGUILayout.Toggle(
            new GUIContent("Create LOD", "If checked, creates a LOD (Level of Detail) setup with the original object and the impostor."),
            createLOD);

        // New Apply to Prefab toggle
        applyToPrefab = EditorGUILayout.Toggle(
            new GUIContent("Apply to Prefab", "If checked and the source object is a prefab, apply changes to the prefab (LOD group, impostor)."),
            applyToPrefab);

        markAsStatic = EditorGUILayout.Toggle(
            new GUIContent("Mark as Static", "If checked, marks the impostor GameObject as static."),
            markAsStatic);

        if (GUILayout.Button("Generate Impostor"))
        {
            if (sourceObject == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Source Object.", "OK");
            }
            else
            {
                CreateImpostor();
            }
        }

        if (GUILayout.Button("Undo"))
        {
            Undo.PerformUndo();
            statusMessage = "Undo performed.";
        }

        // Display status messages
        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
        }
    }

    void CreateImpostor()
    {
        statusMessage = ""; // Clear previous status

        // Ensure the ImpostorFiles/Meshes, ImpostorFiles/Materials, and ImpostorFiles/Textures folders exist
        string impostorFolder = "Assets/ImpostorFiles";
        string meshesFolder = Path.Combine(impostorFolder, "Meshes");
        string materialsFolder = Path.Combine(impostorFolder, "Materials");
        string texturesFolder = Path.Combine(impostorFolder, "Textures"); // New Textures folder

        if (!AssetDatabase.IsValidFolder(impostorFolder))
        {
            AssetDatabase.CreateFolder("Assets", "ImpostorFiles");
        }
        if (!AssetDatabase.IsValidFolder(meshesFolder))
        {
            AssetDatabase.CreateFolder(impostorFolder, "Meshes");
        }
        if (!AssetDatabase.IsValidFolder(materialsFolder))
        {
            AssetDatabase.CreateFolder(impostorFolder, "Materials");
        }
        if (!AssetDatabase.IsValidFolder(texturesFolder))
        {
            AssetDatabase.CreateFolder(impostorFolder, "Textures"); // Create Textures folder
        }

        // Create a unique identifier for asset naming
        string uniqueID = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");

        // **Begin Layer Management**
        // Store original layers
        StoreOriginalLayers(sourceObject);

        try
        {
            // Set layer of source object and its children to BakingLayer
            SetLayerRecursively(sourceObject, BakingLayer);

            // Create a temporary camera
            Camera captureCamera = new GameObject("ImpostorCamera").AddComponent<Camera>();

            // Setup camera
            captureCamera.clearFlags = CameraClearFlags.SolidColor;
            captureCamera.backgroundColor = Color.black; // Unique background color for trimming
            captureCamera.orthographic = true;
            captureCamera.aspect = 1f; // Ensure the camera aspect ratio is 1

            // Set the camera's culling mask to only include the BakingLayer
            captureCamera.cullingMask = 1 << BakingLayer;

            Bounds bounds = CalculateBounds(sourceObject);

            textureSize = textureSizeOptions[selectedTextureSizeIndex];

            // Create RenderTexture
            RenderTexture rt = new RenderTexture(textureSize, textureSize, 24);
            captureCamera.targetTexture = rt;

            Vector3[] directions = new Vector3[]
            {
                Vector3.forward,    // Front (+Z)
                Vector3.back,       // Back (-Z)
                Vector3.left,       // Left (-X)
                Vector3.right,      // Right (+X)
                Vector3.up,         // Up (+Y)
                Vector3.down        // Down (-Y)
            };

            Texture2D[] faceTextures = new Texture2D[6];

            // Capture images
            for (int i = 0; i < 6; i++)
            {
                Vector3 direction = directions[i];

                // Compute orthographic size for this direction
                float orthoSize = GetOrthographicSizeForDirection(direction, bounds);
                captureCamera.orthographicSize = orthoSize;

                // Position the camera
                captureCamera.transform.position = bounds.center - direction * (bounds.extents.magnitude * 1.1f);
                captureCamera.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);

                // Adjust near and far clip planes
                captureCamera.nearClipPlane = -bounds.extents.magnitude * 2f;
                captureCamera.farClipPlane = bounds.extents.magnitude * 2f;

                // Adjust for upside-down images on certain axes
                bool rotate180 = false;
                if (direction == Vector3.down || direction == Vector3.up)
                {
                    captureCamera.transform.Rotate(180, 0, 0);
                    rotate180 = true;
                }

                captureCamera.Render();

                RenderTexture.active = rt;
                Texture2D image = new Texture2D(textureSize, textureSize, TextureFormat.RGB24, false); // Changed to RGB24
                image.ReadPixels(new Rect(0, 0, textureSize, textureSize), 0, 0);
                image.Apply();

                // Trim the background based on unique background color
                Texture2D trimmedImage = TrimTextureByColor(image, captureCamera.backgroundColor, trimAmount);

                // If the camera was rotated 180 degrees, flip the texture vertically to correct orientation
                if (rotate180)
                {
                    FlipTexture(ref trimmedImage, flipY: true, flipX: false);
                }

                faceTextures[i] = trimmedImage;

                // Clean up temporary textures
                DestroyImmediate(image);
            }

            // Swap Front and Back
            Texture2D temp = faceTextures[0];
            faceTextures[0] = faceTextures[1];
            faceTextures[1] = temp;

            // Swap Left and Right
            Texture2D tempLR = faceTextures[2];
            faceTextures[2] = faceTextures[3];
            faceTextures[3] = tempLR;

            // **Flip Front, Back, Left, and Right textures horizontally**
            for (int i = 0; i < 4; i++) // Indices 0 to 3 correspond to Front, Back, Left, Right
            {
                FlipTexture(ref faceTextures[i], flipY: false, flipX: true);
            }

            // Create square atlas
            Texture2D atlas = new Texture2D(textureSize, textureSize, TextureFormat.RGB24, false); // Changed to RGB24
            atlas.filterMode = FilterMode.Bilinear;
            atlas.wrapMode = TextureWrapMode.Clamp;

            // Calculate tile sizes to fit into the square atlas (2x3 grid)
            int tileWidth = textureSize / 2;
            int tileHeight = textureSize / 3;

            // Resize face textures to fit into the atlas tiles
            for (int i = 0; i < faceTextures.Length; i++)
            {
                Texture2D resizedImage = ResizeTexture(faceTextures[i], tileWidth, tileHeight);
                DestroyImmediate(faceTextures[i]);
                faceTextures[i] = resizedImage;
            }

            // Arrange the textures in the atlas
            // Positions for the 2x3 grid
            Vector2Int[] positions = new Vector2Int[]
            {
                new Vector2Int(tileWidth * 0, tileHeight * 2), // Face 0 (Front +Z)
                new Vector2Int(tileWidth * 1, tileHeight * 2), // Face 1 (Back -Z)
                new Vector2Int(tileWidth * 0, tileHeight * 1), // Face 2 (Left -X)
                new Vector2Int(tileWidth * 1, tileHeight * 1), // Face 3 (Right +X)
                new Vector2Int(tileWidth * 0, tileHeight * 0), // Face 4 (Up +Y)
                new Vector2Int(tileWidth * 1, tileHeight * 0)  // Face 5 (Down -Y)
            };

            // Set the pixels into the atlas
            for (int i = 0; i < faceTextures.Length; i++)
            {
                atlas.SetPixels(positions[i].x, positions[i].y, tileWidth, tileHeight, faceTextures[i].GetPixels());
            }

            atlas.Apply();

            // **Save atlas texture with unique name in the Textures folder**
            string atlasPath = Path.Combine(texturesFolder, $"ImpostorAtlas_{uniqueID}.png");
            File.WriteAllBytes(atlasPath, atlas.EncodeToPNG());
            AssetDatabase.ImportAsset(atlasPath);
            Texture2D atlasAsset = (Texture2D)AssetDatabase.LoadAssetAtPath(atlasPath, typeof(Texture2D));

            // Set texture import settings
            TextureImporter importer = (TextureImporter)TextureImporter.GetAtPath(atlasPath);
            importer.isReadable = true;
            importer.alphaIsTransparency = false; // Disabled since we're using RGB
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            // Create material with selected shader
            Material impostorMaterial = new Material(selectedShader != null ? selectedShader : Shader.Find("Standard"));
            impostorMaterial.mainTexture = atlasAsset;

            // Save material with unique name
            string materialPath = Path.Combine(materialsFolder, $"ImpostorMaterial_{uniqueID}.mat");
            AssetDatabase.CreateAsset(impostorMaterial, materialPath);

            // Create custom cube mesh and save it with unique name
            Mesh cubeMesh = CreateCubeMesh();
            string meshPath = Path.Combine(meshesFolder, $"ImpostorMesh_{uniqueID}.asset");
            AssetDatabase.CreateAsset(cubeMesh, meshPath);

            // Create GameObject with custom mesh
            GameObject impostorCube = new GameObject(sourceObject.name + "_Impostor", typeof(MeshFilter), typeof(MeshRenderer));
            impostorCube.transform.position = bounds.center;
            impostorCube.transform.localScale = bounds.size;
            impostorCube.GetComponent<MeshFilter>().mesh = cubeMesh;
            impostorCube.GetComponent<Renderer>().sharedMaterial = impostorMaterial;

            // Register creation for Undo
            Undo.RegisterCreatedObjectUndo(impostorCube, "Create Impostor Cube");

            // Add LODGroup if selected
            if (createLOD)
            {
                LODGroup lodGroup = sourceObject.GetComponent<LODGroup>();
                if (lodGroup == null)
                {
                    // If no LODGroup exists, add one
                    lodGroup = Undo.AddComponent<LODGroup>(sourceObject);
                    // Register creation for Undo
                    Undo.RegisterCreatedObjectUndo(lodGroup, "Create LOD Group");
                }

                // Get all renderers from the source object and its children
                Renderer[] originalRenderers = sourceObject.GetComponentsInChildren<Renderer>();

                // Remove the impostor renderer from the original renderers if it's there
                originalRenderers = originalRenderers.Where(r => r.gameObject != impostorCube).ToArray();

                // Create LOD0 with original renderers (higher detail)
                LOD lod0 = new LOD(0.5f, originalRenderers);

                // Create LOD1 with impostor renderer (lower detail)
                Renderer impostorRenderer = impostorCube.GetComponent<Renderer>();
                LOD lod1 = new LOD(0.01f, new Renderer[] { impostorRenderer });

                // Assign the two LOD levels to the LODGroup
                lodGroup.SetLODs(new LOD[] { lod0, lod1 });
                lodGroup.RecalculateBounds();

                // Assign impostor as child of source object
                impostorCube.transform.SetParent(sourceObject.transform);

                // Update status message
                statusMessage = $"Impostor added as LOD level 1. Total LOD levels: 2.";
            }
            else
            {
                // If not creating LOD, ensure impostor has no parent
                impostorCube.transform.SetParent(null);
                statusMessage = "Impostor created without LOD.";
            }
            impostorCube.isStatic = markAsStatic;

            // Handle Apply to Prefab
            if (applyToPrefab)
            {
                // Check if sourceObject is part of a prefab instance
                if (PrefabUtility.IsPartOfPrefabInstance(sourceObject))
                {
                    // Apply changes to the prefab
                    PrefabUtility.ApplyPrefabInstance(sourceObject, InteractionMode.UserAction);
                    statusMessage += "\nChanges applied to prefab.";
                }
                else if (PrefabUtility.IsPartOfPrefabAsset(sourceObject))
                {
                    // If it's a prefab asset, apply directly
                    // Note: Modifying prefab assets directly can be risky; ensure this is intended
                    PrefabUtility.RecordPrefabInstancePropertyModifications(sourceObject);
                    statusMessage += "\nChanges recorded for prefab asset.";
                }
                else
                {
                    EditorUtility.DisplayDialog("Warning", "Source Object is not a prefab instance or asset. 'Apply to Prefab' has no effect.", "OK");
                }
            }

            // Clean up temporary camera
            DestroyImmediate(captureCamera.gameObject);

            // Clean up
            foreach (var tex in faceTextures)
            {
                DestroyImmediate(tex);
            }
            DestroyImmediate(rt);
            DestroyImmediate(atlas);

            // Save assets
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("Success", "Impostor created successfully!", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError(ex.Message);
            EditorUtility.DisplayDialog("Error", "An error occurred. Please check the console for details.", "OK");
        }
        finally
        {
            // Restore original layers
            RestoreOriginalLayers();
        }


        /// <summary>
        /// Stores the original layers of the source object and all its children.
        /// </summary>
        /// <param name="obj">The root GameObject.</param>
        void StoreOriginalLayers(GameObject obj)
        {
            originalLayers.Clear();
            foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
            {
                originalLayers[child.gameObject] = child.gameObject.layer;
            }
        }

        /// <summary>
        /// Restores the original layers of the source object and all its children.
        /// </summary>
        void RestoreOriginalLayers()
        {
            foreach (var kvp in originalLayers)
            {
                if (kvp.Key != null)
                {
                    kvp.Key.layer = kvp.Value;
                }
            }
            originalLayers.Clear();
        }

        /// <summary>
        /// Sets the layer of the GameObject and all its children recursively.
        /// </summary>
        /// <param name="obj">The root GameObject.</param>
        /// <param name="layer">The layer to set.</param>
        void SetLayerRecursively(GameObject obj, int layer)
        {
            if (obj == null) return;
            obj.layer = layer;
            foreach (Transform child in obj.transform)
            {
                if (child != null)
                {
                    SetLayerRecursively(child.gameObject, layer);
                }
            }
        }

        float GetOrthographicSizeForDirection(Vector3 direction, Bounds bounds)
        {
            // Calculate tight orthographic size to eliminate black borders

            // Determine up and right vectors for the camera
            Vector3 up = Vector3.up;
            Vector3 right = Vector3.Cross(direction, up);
            if (right == Vector3.zero)
            {
                // If direction is parallel to up vector, choose a different up vector
                up = Vector3.forward;
                right = Vector3.Cross(direction, up);
            }
            up = Vector3.Cross(right, direction);

            // Get the 8 corners of the bounding box
            Vector3[] corners = new Vector3[8];
            int i = 0;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        corners[i++] = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                    }
                }
            }

            // Project the corners onto the camera's plane
            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;

            foreach (var corner in corners)
            {
                // Compute vector from camera position to corner
                Vector3 toCorner = corner - (bounds.center - direction * bounds.extents.magnitude * 1.1f);

                // Project onto right and up vectors
                float u = Vector3.Dot(toCorner, right);
                float v = Vector3.Dot(toCorner, up);

                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            // Compute half spans
            float halfHorizontalSpan = (maxU - minU) / 2f;
            float halfVerticalSpan = (maxV - minV) / 2f;

            // Orthographic size is the maximum of the half spans
            float orthoSize = Mathf.Max(halfHorizontalSpan, halfVerticalSpan);

            return orthoSize;
        }

        Bounds CalculateBounds(GameObject obj)
        {
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(obj.transform.position, Vector3.zero);
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer rend in renderers)
            {
                bounds.Encapsulate(rend.bounds);
            }
            return bounds;
        }

        Mesh CreateCubeMesh()
        {
            Mesh mesh = new Mesh();

            // Vertices (unchanged)
            Vector3[] vertices = new Vector3[24]
            {
                // Front face
                new Vector3(-0.5f, -0.5f,  0.5f), // 0
                new Vector3( 0.5f, -0.5f,  0.5f), // 1
                new Vector3( 0.5f,  0.5f,  0.5f), // 2
                new Vector3(-0.5f,  0.5f,  0.5f), // 3

                // Back face
                new Vector3( 0.5f, -0.5f, -0.5f), // 4
                new Vector3(-0.5f, -0.5f, -0.5f), // 5
                new Vector3(-0.5f,  0.5f, -0.5f), // 6
                new Vector3( 0.5f,  0.5f, -0.5f), // 7

                // Left face
                new Vector3(-0.5f, -0.5f, -0.5f), // 8
                new Vector3(-0.5f, -0.5f,  0.5f), // 9
                new Vector3(-0.5f,  0.5f,  0.5f), //10
                new Vector3(-0.5f,  0.5f, -0.5f), //11

                // Right face
                new Vector3( 0.5f, -0.5f,  0.5f), //12
                new Vector3( 0.5f, -0.5f, -0.5f), //13
                new Vector3( 0.5f,  0.5f, -0.5f), //14
                new Vector3( 0.5f,  0.5f,  0.5f), //15

                // Top face
                new Vector3(-0.5f,  0.5f,  0.5f), //16
                new Vector3( 0.5f,  0.5f,  0.5f), //17
                new Vector3( 0.5f,  0.5f, -0.5f), //18
                new Vector3(-0.5f,  0.5f, -0.5f), //19

                // Bottom face
                new Vector3(-0.5f, -0.5f, -0.5f), //20
                new Vector3( 0.5f, -0.5f, -0.5f), //21
                new Vector3( 0.5f, -0.5f,  0.5f), //22
                new Vector3(-0.5f, -0.5f,  0.5f), //23
            };

            // Triangles (unchanged)
            int[] triangles = new int[36]
            {
                // Front face
                0, 1, 2,
                0, 2, 3,

                // Back face
                4, 5, 6,
                4, 6, 7,

                // Left face
                8, 9,10,
                8,10,11,

                // Right face
                12,13,14,
                12,14,15,

                // Top face
                16,17,18,
                16,18,19,

                // Bottom face
                20,21,22,
                20,22,23
            };

            // UVs
            Vector2[] uvs = new Vector2[24];

            float tileWidth = 1f / 2f;
            float tileHeight = 1f / 3f;

            // UV mapping ranges for each face
            Vector2[] uvStart = new Vector2[6];
            Vector2[] uvEnd = new Vector2[6];

            // Positions in the atlas (normalized UV coordinates)
            uvStart[0] = new Vector2(0f * tileWidth, 2f * tileHeight); // Front (+Z)
            uvEnd[0] = new Vector2(1f * tileWidth, 3f * tileHeight);

            uvStart[1] = new Vector2(1f * tileWidth, 2f * tileHeight); // Back (-Z)
            uvEnd[1] = new Vector2(2f * tileWidth, 3f * tileHeight);

            uvStart[2] = new Vector2(0f * tileWidth, 1f * tileHeight); // Left (-X)
            uvEnd[2] = new Vector2(1f * tileWidth, 2f * tileHeight);

            uvStart[3] = new Vector2(1f * tileWidth, 1f * tileHeight); // Right (+X)
            uvEnd[3] = new Vector2(2f * tileWidth, 2f * tileHeight);

            uvStart[4] = new Vector2(0f * tileWidth, 0f * tileHeight); // Up (+Y)
            uvEnd[4] = new Vector2(1f * tileWidth, 1f * tileHeight);

            uvStart[5] = new Vector2(1f * tileWidth, 0f * tileHeight); // Down (-Y)
            uvEnd[5] = new Vector2(2f * tileWidth, 1f * tileHeight);

            // Assign UVs for each face
            SetFaceUVs(uvs, 0, uvStart[0], uvEnd[0], false); // Front face
            SetFaceUVs(uvs, 4, uvStart[1], uvEnd[1], false); // Back face
            SetFaceUVs(uvs, 8, uvStart[2], uvEnd[2], false); // Left face
            SetFaceUVs(uvs, 12, uvStart[3], uvEnd[3], false); // Right face
            SetFaceUVs(uvs, 16, uvStart[4], uvEnd[4], false); // Top face
            SetFaceUVs(uvs, 20, uvStart[5], uvEnd[5], false); // Bottom face

            // Normals (unchanged)
            Vector3[] normals = new Vector3[24];
            // Front
            for (int i = 0; i < 4; i++) normals[i] = Vector3.forward;
            // Back
            for (int i = 4; i < 8; i++) normals[i] = Vector3.back;
            // Left
            for (int i = 8; i < 12; i++) normals[i] = Vector3.left;
            // Right
            for (int i = 12; i < 16; i++) normals[i] = Vector3.right;
            // Top
            for (int i = 16; i < 20; i++) normals[i] = Vector3.up;
            // Bottom
            for (int i = 20; i < 24; i++) normals[i] = Vector3.down;

            // Assign to mesh
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            mesh.RecalculateBounds();

            return mesh;
        }

        void SetFaceUVs(Vector2[] uvs, int vertIndex, Vector2 uvStart, Vector2 uvEnd, bool rotate180)
        {
            if (!rotate180)
            {
                // Standard UV mapping
                uvs[vertIndex + 0] = new Vector2(uvStart.x, uvStart.y);
                uvs[vertIndex + 1] = new Vector2(uvEnd.x, uvStart.y);
                uvs[vertIndex + 2] = new Vector2(uvEnd.x, uvEnd.y);
                uvs[vertIndex + 3] = new Vector2(uvStart.x, uvEnd.y);
            }
            else
            {
                // Rotated 180 degrees
                uvs[vertIndex + 0] = new Vector2(uvEnd.x, uvEnd.y);
                uvs[vertIndex + 1] = new Vector2(uvStart.x, uvEnd.y);
                uvs[vertIndex + 2] = new Vector2(uvStart.x, uvStart.y);
                uvs[vertIndex + 3] = new Vector2(uvEnd.x, uvStart.y);
            }
        }

        // Modified TrimTexture to trim based on color instead of alpha
        Texture2D TrimTextureByColor(Texture2D source, Color backgroundColor, float trimAmount)
        {
            int width = source.width;
            int height = source.height;

            Color[] pixels = source.GetPixels();

            int xMin = width;
            int xMax = 0;
            int yMin = height;
            int yMax = 0;

            // Define a threshold to determine if a pixel is part of the background
            float threshold = 0.1f; // Adjust as necessary

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color pixel = pixels[y * width + x];
                    if (!IsColorSimilar(pixel, backgroundColor, threshold))
                    {
                        if (x < xMin) xMin = x;
                        if (x > xMax) xMax = x;
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                    }
                }
            }

            // Check if the entire image is background
            if (xMax < xMin || yMax < yMin)
            {
                // Return a small texture with average color
                Color avgColor = ComputeAverageColor(pixels, backgroundColor, threshold);
                Texture2D smallTex = new Texture2D(1, 1, TextureFormat.RGB24, false);
                smallTex.SetPixel(0, 0, avgColor);
                smallTex.Apply();
                return smallTex;
            }

            // Adjust trimming based on trimAmount
            int offsetX = Mathf.RoundToInt((xMax - xMin) * trimAmount);
            int offsetY = Mathf.RoundToInt((yMax - yMin) * trimAmount);

            xMin = Mathf.Clamp(xMin - offsetX, 0, width - 1);
            xMax = Mathf.Clamp(xMax + offsetX, 0, width - 1);
            yMin = Mathf.Clamp(yMin - offsetY, 0, height - 1);
            yMax = Mathf.Clamp(yMax + offsetY, 0, height - 1);

            int trimmedWidth = xMax - xMin + 1;
            int trimmedHeight = yMax - yMin + 1;

            Color[] trimmedPixels = new Color[trimmedWidth * trimmedHeight];

            for (int y = 0; y < trimmedHeight; y++)
            {
                for (int x = 0; x < trimmedWidth; x++)
                {
                    trimmedPixels[y * trimmedWidth + x] = pixels[(yMin + y) * width + (xMin + x)];
                }
            }

            // Compute average color of non-background pixels
            Color averageColor = ComputeAverageColor(trimmedPixels, backgroundColor, threshold);

            // Replace background pixels with average color
            for (int i = 0; i < trimmedPixels.Length; i++)
            {
                if (!IsColorSimilar(trimmedPixels[i], backgroundColor, threshold))
                {
                    // Keep the original pixel
                }
                else
                {
                    // Replace with average color
                    trimmedPixels[i] = averageColor;
                }
            }

            Texture2D trimmedTexture = new Texture2D(trimmedWidth, trimmedHeight, TextureFormat.RGB24, false);
            trimmedTexture.SetPixels(trimmedPixels);
            trimmedTexture.Apply();

            return trimmedTexture;
        }

        // Helper method to check color similarity
        bool IsColorSimilar(Color a, Color b, float threshold)
        {
            float distance = Vector3.Distance(new Vector3(a.r, a.g, a.b), new Vector3(b.r, b.g, b.b));
            return distance < threshold;
        }

        // Helper method to compute average color of non-background pixels
        Color ComputeAverageColor(Color[] pixels, Color backgroundColor, float threshold)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (var pixel in pixels)
            {
                if (!IsColorSimilar(pixel, backgroundColor, threshold))
                {
                    sum += new Vector3(pixel.r, pixel.g, pixel.b);
                    count++;
                }
            }
            if (count == 0)
                return backgroundColor;
            Vector3 avg = sum / count;
            return new Color(avg.x, avg.y, avg.z, 1f);
        }

        // Overloaded TrimTexture to handle flipping
        void FlipTexture(ref Texture2D texture, bool flipY, bool flipX)
        {
            Texture2D flipped = new Texture2D(texture.width, texture.height, texture.format, false);
            Color[] pixels = texture.GetPixels();

            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    int newX = flipX ? (texture.width - 1 - x) : x;
                    int newY = flipY ? (texture.height - 1 - y) : y;
                    flipped.SetPixel(newX, newY, pixels[y * texture.width + x]);
                }
            }

            flipped.Apply();
            texture = flipped;
        }

        Texture2D ResizeTexture(Texture2D source, int newWidth, int newHeight)
        {
            RenderTexture rt = RenderTexture.GetTemporary(newWidth, newHeight);
            rt.filterMode = FilterMode.Bilinear;

            Graphics.Blit(source, rt);

            RenderTexture.active = rt;
            Texture2D result = new Texture2D(newWidth, newHeight, TextureFormat.RGB24, false); // Changed to RGB24
            result.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            return result;
        }
    }
}
