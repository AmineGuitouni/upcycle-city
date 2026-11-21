using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Player;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Gives a player rig the Character Creator look: first-person arms in the player's colours and a
    /// shadow-only body. Idempotent, so it is safe to run again; PlayerGymBuilder calls it for new rigs.
    /// </summary>
    static class PlayerCharacterSetup
    {
        const string PlayerPrefabPath = "Assets/UpcycleCity/Prefabs/Player/Player.prefab";
        const string BodyName = "BodyShadow";

        [MenuItem("Upcycle City/Character/Add Character To Player Prefab", priority = 22)]
        static void ApplyToPlayerPrefab()
        {
            CharacterPartsLibrary library = CharacterCreatorBuilder.CreateOrFillLibrary();
            GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                Apply(root, library);
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            Debug.Log($"[Upcycle City] {PlayerPrefabPath}: first-person arms and shadow body set up.");
        }

        public static void Apply(GameObject playerRoot, CharacterPartsLibrary library)
        {
            if (playerRoot == null || library == null)
                return;

            FirstPersonArms arms = playerRoot.GetComponent<FirstPersonArms>() ?? playerRoot.AddComponent<FirstPersonArms>();
            arms.SetLibrary(library);
            EditorUtility.SetDirty(arms);

            Transform body = playerRoot.transform.Find(BodyName);
            if (body == null)
            {
                body = new GameObject(BodyName).transform;
                body.SetParent(playerRoot.transform, false);
            }
            body.gameObject.layer = GameLayers.Player;
            CharacterAvatar avatar = body.GetComponent<CharacterAvatar>() ?? body.gameObject.AddComponent<CharacterAvatar>();
            avatar.SetLibrary(library);
            avatar.Configure(followSavedAppearance: true, ShadowCastingMode.ShadowsOnly);
            if (body.GetComponent<PlayerBodyShadow>() == null)
                body.gameObject.AddComponent<PlayerBodyShadow>();
            EditorUtility.SetDirty(avatar);
        }
    }
}
