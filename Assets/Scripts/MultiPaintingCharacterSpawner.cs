using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class MultiPaintingCharacterSpawner : MonoBehaviour
{
    [Serializable]
    public class PaintingEntry
    {
        [Tooltip("Must exactly match the Name in the XR Reference Image Library.")]
        public string imageName;

        [Tooltip("Prefab containing the character and this painting's information panel.")]
        public GameObject characterPrefab;

        [Tooltip("Character appears only when the camera is within this distance, in metres.")]
        public float activationDistance = 2.5f;

        [Tooltip("Positive/negative values choose opposite sides of the painting.")]
        public float sideOffset = 1.1f;

        [Tooltip("Moves the character away from the wall and into the room.")]
        public float wallOffset = 0.35f;

        [Tooltip("Use 0 when the prefab root pivot is at the character's feet.")]
        public float characterGroundOffset = 0f;

        [Tooltip("Use 0 normally; try 90, -90, or 180 if the model faces incorrectly.")]
        public float yawOffset = 0f;
    }

    [Header("AR Components")]
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private Transform arCamera;

    [Header("Museum Paintings")]
    [SerializeField] private List<PaintingEntry> paintings = new();

    private readonly Dictionary<string, ARTrackedImage> trackedImages = new();
    private readonly Dictionary<string, GameObject> spawnedCharacters = new();
    private readonly HashSet<string> lockedPlacements = new();

    private void Awake()
    {
        if (trackedImageManager == null)
            Debug.LogError("Tracked Image Manager is not assigned.", this);

        if (planeManager == null)
            Debug.LogError("Plane Manager is not assigned.", this);

        if (arCamera == null && Camera.main != null)
            arCamera = Camera.main.transform;
    }

    private void OnEnable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);
    }

    private void OnDisable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
    }

    private void Update()
    {
        if (arCamera == null)
            return;

        PaintingEntry nearestEntry = null;
        ARTrackedImage nearestImage = null;
        float nearestDistance = float.MaxValue;

        foreach (PaintingEntry entry in paintings)
        {
            if (entry == null ||
                string.IsNullOrWhiteSpace(entry.imageName) ||
                entry.characterPrefab == null)
            {
                continue;
            }

            if (!trackedImages.TryGetValue(entry.imageName, out ARTrackedImage trackedImage) ||
                trackedImage == null ||
                trackedImage.trackingState != TrackingState.Tracking)
            {
                continue;
            }

            float distance = Vector3.Distance(
                arCamera.position,
                trackedImage.transform.position);

            if (distance <= entry.activationDistance &&
                distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestEntry = entry;
                nearestImage = trackedImage;
            }
        }

        foreach (PaintingEntry entry in paintings)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.imageName))
                continue;

            bool shouldShow = entry == nearestEntry;

            if (!shouldShow)
            {
                SetCharacterVisible(entry.imageName, false);
                continue;
            }

            EnsureCharacterPlaced(nearestEntry, nearestImage);
            SetCharacterVisible(entry.imageName, true);
            FaceCharacterTowardCamera(entry);
        }
    }

    private void OnTrackedImagesChanged(
        ARTrackablesChangedEventArgs<ARTrackedImage> eventArgs)
    {
        foreach (ARTrackedImage trackedImage in eventArgs.added)
            RegisterTrackedImage(trackedImage);

        foreach (ARTrackedImage trackedImage in eventArgs.updated)
            RegisterTrackedImage(trackedImage);

        foreach (var removedImage in eventArgs.removed)
        {
            ARTrackedImage trackedImage = removedImage.Value;

            if (trackedImage == null)
                continue;

            string imageName = trackedImage.referenceImage.name;
            trackedImages.Remove(imageName);
            lockedPlacements.Remove(imageName);
            SetCharacterVisible(imageName, false);
        }
    }

    private void RegisterTrackedImage(ARTrackedImage trackedImage)
    {
        if (trackedImage == null)
            return;

        trackedImages[trackedImage.referenceImage.name] = trackedImage;
    }

    private void EnsureCharacterPlaced(
        PaintingEntry entry,
        ARTrackedImage trackedImage)
    {
        if (entry == null || trackedImage == null)
            return;

        if (spawnedCharacters.ContainsKey(entry.imageName) &&
            lockedPlacements.Contains(entry.imageName))
        {
            return;
        }

        if (!TryGetFloorHeight(
                trackedImage.transform.position.y,
                out float floorY))
        {
            Debug.LogWarning(
                $"No horizontal floor plane available for {entry.imageName}.",
                this);

            SetCharacterVisible(entry.imageName, false);
            return;
        }

        Vector3 imagePosition = trackedImage.transform.position;

        Vector3 sideDirection = Vector3.ProjectOnPlane(
            trackedImage.transform.right,
            Vector3.up);

        if (sideDirection.sqrMagnitude < 0.001f)
            sideDirection = Vector3.right;

        sideDirection.Normalize();

        Vector3 wallNormal = Vector3.ProjectOnPlane(
            trackedImage.transform.up,
            Vector3.up);

        if (wallNormal.sqrMagnitude < 0.001f)
            wallNormal = Vector3.forward;

        wallNormal.Normalize();

        Vector3 directionToCamera =
            arCamera.position - imagePosition;

        directionToCamera.y = 0f;

        if (Vector3.Dot(wallNormal, directionToCamera) < 0f)
            wallNormal = -wallNormal;

        Vector3 characterPosition =
            imagePosition +
            sideDirection * entry.sideOffset +
            wallNormal * entry.wallOffset;

        characterPosition.y =
            floorY + entry.characterGroundOffset;

        if (!spawnedCharacters.TryGetValue(
                entry.imageName,
                out GameObject character) ||
            character == null)
        {
            character = Instantiate(
                entry.characterPrefab,
                characterPosition,
                Quaternion.identity);

            character.name = $"GuideCharacter_{entry.imageName}";
            spawnedCharacters[entry.imageName] = character;
        }

        character.transform.position = characterPosition;
        lockedPlacements.Add(entry.imageName);

        Debug.Log(
            $"Placed character for {entry.imageName} at floor Y={floorY:F3}.",
            this);
    }

    private bool TryGetFloorHeight(
        float paintingY,
        out float floorY)
    {
        floorY = 0f;
        float largestArea = -1f;
        bool found = false;

        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane == null ||
                plane.trackingState != TrackingState.Tracking)
            {
                continue;
            }

            bool facesUp =
                plane.alignment == PlaneAlignment.HorizontalUp ||
                Vector3.Dot(plane.transform.up, Vector3.up) > 0.9f;

            if (!facesUp)
                continue;

            float candidateY = plane.transform.position.y;

            if (candidateY >= paintingY)
                continue;

            float area = plane.size.x * plane.size.y;

            if (area > largestArea)
            {
                largestArea = area;
                floorY = candidateY;
                found = true;
            }
        }

        return found;
    }

    private void FaceCharacterTowardCamera(PaintingEntry entry)
    {
        if (entry == null ||
            !spawnedCharacters.TryGetValue(
                entry.imageName,
                out GameObject character) ||
            character == null ||
            arCamera == null)
        {
            return;
        }

        Vector3 targetPosition = arCamera.position;
        targetPosition.y = character.transform.position.y;

        Vector3 lookDirection =
            targetPosition - character.transform.position;

        if (lookDirection.sqrMagnitude < 0.001f)
            return;

        Quaternion lookRotation =
            Quaternion.LookRotation(
                lookDirection.normalized,
                Vector3.up);

        character.transform.rotation =
            lookRotation *
            Quaternion.Euler(0f, entry.yawOffset, 0f);
    }

    private void SetCharacterVisible(
        string imageName,
        bool visible)
    {
        if (spawnedCharacters.TryGetValue(
                imageName,
                out GameObject character) &&
            character != null &&
            character.activeSelf != visible)
        {
            character.SetActive(visible);
        }
    }
}