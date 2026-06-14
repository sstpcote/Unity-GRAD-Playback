using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ========================== //
//      CUSTOM EDITOR         //
// ========================== //
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;

[CustomEditor(typeof(GameManager))]
public class MyScriptEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var gm = (GameManager)target;

        EditorGUILayout.LabelField("Eye Tracking Playback", EditorStyles.boldLabel);

        // Affiche les sélections actuelles (read-only)
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Current selection", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("Player Log:", string.IsNullOrEmpty(gm.selectedFilePlayer) ? "<none>" : gm.selectedFilePlayer);
            EditorGUILayout.LabelField("ROI/Data:", string.IsNullOrEmpty(gm.selectedFileData) ? "<none>" : gm.selectedFileData);
        }

        EditorGUILayout.Space(6);

        // Boutons d’ouverture de boîte de dialogue
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Choose Player Log…", GUILayout.Height(26)))
                PickResourceFileInto(ref gm.selectedFilePlayer);

            if (GUILayout.Button("Choose ROI/Data…", GUILayout.Height(26)))
                PickResourceFileInto(ref gm.selectedFileData);
        }

        EditorGUILayout.Space(10);

        // Actions
        if (GUILayout.Button("Generate Data (as-displayed)", GUILayout.Height(36)))
        {
            gm.GenerateOutputFile();
        }

        if (GUILayout.Button("Save Visible CSV Buffer", GUILayout.Height(28)))
        {
            gm.SaveVisibleCsv();
        }

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // Laisse visibles les autres champs du GameManager (toggles, etc.)
        DrawDefaultInspector();

        // Forcer sauvegarde de la scène si on a modifié la sélection
        if (GUI.changed)
            EditorUtility.SetDirty(gm);
    }

    /// <summary>
    /// Ouvre un File Panel, exige un fichier sous un dossier Resources/, 
    /// convertit le chemin en nom Resources (sans extension) et l’assigne.
    /// </summary>
    private void PickResourceFileInto(ref string targetField)
    {
        // point de départ : dossier Assets
        string startDir = Application.dataPath;
        // autoriser txt/csv
        string path = EditorUtility.OpenFilePanel("Select TextAsset under a Resources/ folder", startDir, "txt,csv");
        if (string.IsNullOrEmpty(path)) return;

        path = path.Replace('\\', '/');

        // Doit être sous /Resources/
        int idx = path.IndexOf("/Resources/");
        if (idx < 0)
        {
            EditorUtility.DisplayDialog(
                "Not under Resources",
                "Please pick a file located inside an Assets/**/Resources/ folder so it can be loaded via Resources.Load at runtime.",
                "OK");
            return;
        }

        // Chemin relatif à Resources/ et sans extension
        string rel = path.Substring(idx + "/Resources/".Length);
        rel = rel.Substring(0, rel.LastIndexOf('.')); // retire .txt/.csv

        // Sanity: vérifier qu’un TextAsset existe à ce chemin Resources
#if UNITY_EDITOR
        // Convertir en chemin d’asset Unity pour feedback (facultatif)
        string assetPathGuess = "Assets/" + path.Split(new[] { "/Assets/" }, System.StringSplitOptions.None).Last();
        assetPathGuess = assetPathGuess.Replace('\\', '/');
        var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPathGuess);
        if (ta == null)
        {
            // Essayer de retrouver l’asset via recherche si le panel a renvoyé un chemin absolu atypique
            string[] guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(path));
            foreach (var g in guids)
            {
                var ap = AssetDatabase.GUIDToAssetPath(g);
                if (ap.Contains("/Resources/") && Path.GetFileNameWithoutExtension(ap) == Path.GetFileNameWithoutExtension(path))
                {
                    ta = AssetDatabase.LoadAssetAtPath<TextAsset>(ap);
                    break;
                }
            }
        }
        if (ta == null)
        {
            // Pas bloquant si tu es sûr du chemin, mais on prévient
            Debug.LogWarning($"Picked path seems not imported as TextAsset: {path}");
        }
#endif

        targetField = rel; // ← nom Resources (ex: "Sub/MyLogFile")
        Repaint();
    }
}
#endif


// ========================== //
//        GAME MANAGER        //
// ========================== //
public class GameManager : MonoBehaviour
{
    class PositionDataPlayer
    {
        public float time;
        public Vector3 camPose;
        public Vector3 camRotation;                        // Euler monde (Unity)
        public Vector3 combinedEyesGazeOrigin;             // SRanipal: local tête (m)
        public Vector3 combinedEyesGazeDirectionNormalized;// vecteur unitaire SRanipal
        public Vector3 combinedEyesGazeDirection;          // Euler local œil (X=pitch, Y=yaw, Z=0)
    }

    class PositionDataObject
    {
        public float time;
        public Vector3 visagePosition;   // monde
        public Vector3 seinsPosition;    // monde
        public Vector3 genitalPosition;  // monde
    }

    [HideInInspector] [SerializeField] public string selectedFileData = "";
    [HideInInspector] [SerializeField] public string selectedFilePlayer = "";

    private string[] dataPlayer;
    private string[] dataSceneObject;

    private List<PositionDataPlayer> playerPositions;
    private List<PositionDataObject> objectPositions;

    public bool isPlaying = false;
    public int positionPlayback = 0;

    public bool invertAxisCoordinateSystem; // conservé si utile

    private GameObject playerHead;
    private GameObject playerEyeGaze;
    private GameObject visageAvatar;
    private GameObject seinsAvatar;
    private GameObject genitalAvatar;

    private GUIStyle customStyle;
    public Vector3 eyeGazeDirectionNormalized;

    // Buffer CSV “as-displayed” live (optionnel)
    [HideInInspector] public bool recordVisibleCSV = false;
    private List<string> visibleRows = new List<string>();
    private string visibleCsvPath;

    void Start()
    {
        playerPositions = new List<PositionDataPlayer>();
        objectPositions = new List<PositionDataObject>();

        playerHead = GameObject.Find("PlayerHead");
        playerEyeGaze = GameObject.Find("PlayerEyeGaze");
        visageAvatar = GameObject.Find("VisageAvatar");
        seinsAvatar = GameObject.Find("SeinsAvatar");
        genitalAvatar = GameObject.Find("GenitalAvatar");

        LoadFile();

        // Init du buffer “as-displayed” live
        visibleCsvPath = System.IO.Path.Combine(Application.persistentDataPath, "visible_export.csv");
        visibleRows.Clear();
        visibleRows.Add("Frame;Time;EyePosX;EyePosY;EyePosZ;EyeFwdX;EyeFwdY;EyeFwdZ;AngEyeForward;AngEyeFace;AngEyeChest;AngEyeGenital");

        Debug.Log("[GameManager] persistentDataPath = " + Application.persistentDataPath);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPlaying = !isPlaying;
            if (isPlaying) StartCoroutine(PlayData());
            else StopAllCoroutines();
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (positionPlayback < playerPositions.Count)
            {
                isPlaying = false;
                positionPlayback++;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            if (positionPlayback > 0)
            {
                isPlaying = false;
                positionPlayback--;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKey(KeyCode.RightArrow))
        {
            if (positionPlayback < playerPositions.Count)
            {
                isPlaying = false;
                positionPlayback++;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            if (positionPlayback > 0)
            {
                isPlaying = false;
                positionPlayback--;
                PlaybackSpecificFrame(positionPlayback);
            }
        }

        // Raccourcis pratiques
        if (Input.GetKeyDown(KeyCode.R))
        {
            recordVisibleCSV = !recordVisibleCSV;
            Debug.Log("Record as-displayed CSV buffer: " + (recordVisibleCSV ? "ON" : "OFF"));
        }
        if (Input.GetKeyDown(KeyCode.S))
        {
            SaveVisibleCsv();
        }
    }

    private void OnGUI()
    {
        if (customStyle == null)
        {
            customStyle = new GUIStyle(GUI.skin.label);
            Texture2D bgTexture = new Texture2D(1, 1);
            bgTexture.SetPixel(0, 0, Color.black);
            bgTexture.Apply();
            customStyle.normal.background = bgTexture;
            customStyle.normal.textColor = Color.white;
            customStyle.fontSize = 22;
            customStyle.padding = new RectOffset(10, 0, 5, 5);
        }

        if (dataPlayer != null && dataPlayer.Length > 0)
        {
            float angleYeuxDevant = Vector3.Angle(playerEyeGaze.transform.forward, Vector3.forward);

            Vector3 positionPlayerEyes = playerEyeGaze.transform.position;
            Vector3 toPositionVisage = (objectPositions[positionPlayback].visagePosition - positionPlayerEyes).normalized;
            Vector3 toPositionSeins = (objectPositions[positionPlayback].seinsPosition - positionPlayerEyes).normalized;
            Vector3 toPositionGenital = (objectPositions[positionPlayback].genitalPosition - positionPlayerEyes).normalized;

            float angleYeuxVisage = Vector3.Angle(playerEyeGaze.transform.forward, toPositionVisage);
            float angleYeuxSeins = Vector3.Angle(playerEyeGaze.transform.forward, toPositionSeins);
            float angleYeuxGenital = Vector3.Angle(playerEyeGaze.transform.forward, toPositionGenital);

            if (playerPositions[positionPlayback].combinedEyesGazeDirectionNormalized == -Vector3.one)
            {
                angleYeuxDevant = 0;
                angleYeuxVisage = 0;
                angleYeuxSeins = 0;
                angleYeuxGenital = 0;
            }

            GUI.Label(new Rect(10, 10, 300, 40), "Frame: " + positionPlayback, customStyle);
            GUI.Label(new Rect(10, 55, 300, 40), $"Angle Yeux-Devant: {angleYeuxDevant:F3} °", customStyle);
            GUI.Label(new Rect(10, 100, 300, 40), $"Angle Yeux-Visage: {angleYeuxVisage:F3} °", customStyle);
            GUI.Label(new Rect(10, 145, 300, 40), $"Angle Yeux-Seins: {angleYeuxSeins:F3} °", customStyle);
            GUI.Label(new Rect(10, 190, 300, 40), $"Angle Yeux-Genital: {angleYeuxGenital:F3} °", customStyle);
        }
    }

    private void PlaybackSpecificFrame(int i)
    {
        Vector3 cameraRotation = playerPositions[i].camRotation;
        Vector3 combinedEyesGazeDirection = playerPositions[i].combinedEyesGazeDirection;

        if (invertAxisCoordinateSystem)
        {
            cameraRotation = new Vector3(cameraRotation.x, cameraRotation.y, cameraRotation.z);
            combinedEyesGazeDirection = new Vector3(-combinedEyesGazeDirection.y, -combinedEyesGazeDirection.x, combinedEyesGazeDirection.z);
        }

        eyeGazeDirectionNormalized = playerPositions[i].combinedEyesGazeDirectionNormalized;

        playerHead.transform.SetPositionAndRotation(playerPositions[i].camPose, Quaternion.Euler(cameraRotation));

        if (playerPositions[i].combinedEyesGazeDirectionNormalized != -Vector3.one)
        {
            // PlayerEyeGaze = enfant de PlayerHead (local tête)
            playerEyeGaze.transform.localPosition = playerPositions[i].combinedEyesGazeOrigin;
            playerEyeGaze.transform.localRotation = Quaternion.Euler(combinedEyesGazeDirection);
        }

        visageAvatar.transform.position = objectPositions[i].visagePosition;
        seinsAvatar.transform.position = objectPositions[i].seinsPosition;
        genitalAvatar.transform.position = objectPositions[i].genitalPosition;

        // Capture live dans le buffer (optionnel)
        CaptureVisibleFrame(i);
    }

    IEnumerator PlayData()
    {
        if (isPlaying)
        {
            for (int i = positionPlayback; i < playerPositions.Count; i++)
            {
                if (!isPlaying) break;
                PlaybackSpecificFrame(i);
                positionPlayback = i;
                yield return new WaitForEndOfFrame();
            }
        }
    }

    // ========================== //
    //          LOADING           //
    // ========================== //
    void LoadFile()
    {
        TextAsset csvDataPlayer = Resources.Load<TextAsset>(selectedFilePlayer);
        TextAsset csvDataObject = Resources.Load<TextAsset>(selectedFileData);

        dataPlayer = csvDataPlayer.text.Replace(",", ".").Split('\n');
        dataSceneObject = csvDataObject.text.Replace(",", ".").Split('\n');

        ParseDataPlayer();
    }

    void ParseDataPlayer()
    {
        // Player
        for (int i = 1; i < dataPlayer.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(dataPlayer[i])) continue;

            string[] row = dataPlayer[i].Split(new char[] { ';' });
            if (row.Length < 37) continue;

            var temp = new PositionDataPlayer();

            temp.time = float.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture);
            temp.camPose = new Vector3(
                float.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture));

            // camRotation: 3 composantes quaternion (x,y,z) → recompose w → Euler
            temp.camRotation = new Vector3(
                float.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture));
            float x = temp.camRotation.x;
            float y = temp.camRotation.y;
            float z = temp.camRotation.z;
            float w = Mathf.Sqrt(Mathf.Max(0f, 1 - (x * x + y * y + z * z)));
            Quaternion q = new Quaternion(x, y, z, w);
            temp.camRotation = q.eulerAngles;

            temp.combinedEyesGazeOrigin = new Vector3(
                float.Parse(row[34], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[35], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[36], System.Globalization.CultureInfo.InvariantCulture));
            temp.combinedEyesGazeOrigin /= 1000f; // mm → m

            temp.combinedEyesGazeDirectionNormalized = new Vector3(
                float.Parse(row[31], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[32], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[33], System.Globalization.CultureInfo.InvariantCulture));

            Vector3 g = temp.combinedEyesGazeDirectionNormalized;
            float yaw = Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(g.y, g.z) * Mathf.Rad2Deg;
            temp.combinedEyesGazeDirection = new Vector3(yaw, pitch, 0f);
            // temp.combinedEyesGazeDirection = new Vector3(pitch, yaw, 0f); // Unity: X=pitch, Y=yaw

            playerPositions.Add(temp);
        }

        // Objects (ROIs monde)
        for (int i = 1; i < dataSceneObject.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(dataSceneObject[i])) continue;
            string[] row = dataSceneObject[i].Split(new char[] { ';' });
            if (row.Length < 12) continue;

            var tempObject = new PositionDataObject();
            tempObject.visagePosition = new Vector3(
                float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture));
            tempObject.seinsPosition = new Vector3(
                float.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[7], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[8], System.Globalization.CultureInfo.InvariantCulture));
            tempObject.genitalPosition = new Vector3(
                float.Parse(row[9], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[10], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[11], System.Globalization.CultureInfo.InvariantCulture));

            objectPositions.Add(tempObject);
        }
    }

    // ========================== //
    //        EXPORT (CSV)        //
    // ========================== //

    /// <summary>
    /// Export PRINCIPAL: écrit exactement ce qui est AFFICHÉ (as-displayed) sur TOUTES les frames.
    /// </summary>
    public void GenerateOutputFile()
    {
        if (playerPositions == null || playerPositions.Count == 0)
        {
            Debug.LogWarning("No player data loaded.");
            return;
        }

        string outputFilePath = System.IO.Path.Combine(
            Application.persistentDataPath,
            $"{selectedFilePlayer}_{selectedFileData}_output.csv"
        );

        // En-tête
        System.IO.File.WriteAllText(outputFilePath, "Frame;Time;AngleYeuxDevant;AngleYeuxVisage;AngleYeuxSeins;AngleYeuxGenital\n");

        var lines = new System.Text.StringBuilder(4096);

        // IMPORTANT : positionne la scène sur chaque frame, puis lit les transforms
        for (int i = 0; i < playerPositions.Count; i++)
        {
            PlaybackSpecificFrame(i); // met à jour la scène (tête, yeux, ROIs)

            // Lire CE QUI EST A L'ÉCRAN
            Vector3 eyePos = playerEyeGaze.transform.position;
            Vector3 eyeFwd = playerEyeGaze.transform.forward.normalized;

            Vector3 pFace = visageAvatar.transform.position;
            Vector3 pChest = seinsAvatar.transform.position;
            Vector3 pGen = genitalAvatar.transform.position;

            float angleYeuxDevant = Vector3.Angle(eyeFwd, Vector3.forward);
            float angleYeuxVisage = Vector3.Angle(eyeFwd, (pFace - eyePos).normalized);
            float angleYeuxSeins = Vector3.Angle(eyeFwd, (pChest - eyePos).normalized);
            float angleYeuxGenital = Vector3.Angle(eyeFwd, (pGen - eyePos).normalized);

            if (playerPositions[i].combinedEyesGazeDirectionNormalized == -Vector3.one)
            {
                angleYeuxDevant = angleYeuxVisage = angleYeuxSeins = angleYeuxGenital = -1f;
            }

            lines.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "{0};{1:F6};{2:F3};{3:F3};{4:F3};{5:F3}\n",
                i, playerPositions[i].time,
                angleYeuxDevant, angleYeuxVisage, angleYeuxSeins, angleYeuxGenital);
        }

        // Remplacer virgules fr si jamais, puis écrire
        string output = lines.ToString().Replace(',', ';');
        System.IO.File.AppendAllText(outputFilePath, output);

        Debug.Log("Output (as-displayed) CSV generated at: " + outputFilePath);
    }

    /// <summary>
    /// Helper pour l'export en lot (BulkExporter).
    /// </summary>
    public void GenerateOutputFileFor(string playerName, string dataName)
    {
        selectedFilePlayer = playerName;
        selectedFileData = dataName;

        playerPositions = new List<PositionDataPlayer>();
        objectPositions = new List<PositionDataObject>();
        LoadFile();

        GenerateOutputFile();
    }

    /// <summary>
    /// Capture la frame visible dans un buffer (optionnel, pour debug ou log live).
    /// </summary>
    private void CaptureVisibleFrame(int i)
    {
        if (!recordVisibleCSV) return;
        if (i < 0 || i >= playerPositions.Count) return;

        Vector3 eyePos = playerEyeGaze.transform.position;
        Vector3 eyeFwd = playerEyeGaze.transform.forward.normalized;

        Vector3 pFace = visageAvatar.transform.position;
        Vector3 pChest = seinsAvatar.transform.position;
        Vector3 pGen = genitalAvatar.transform.position;

        float angEyeForward = Vector3.Angle(eyeFwd, Vector3.forward);
        float angFace = Vector3.Angle(eyeFwd, (pFace - eyePos).normalized);
        float angChest = Vector3.Angle(eyeFwd, (pChest - eyePos).normalized);
        float angGenital = Vector3.Angle(eyeFwd, (pGen - eyePos).normalized);

        float t = playerPositions[i].time;

        string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0};{1:F6};{2:F6};{3:F6};{4:F6};{5:F6};{6:F6};{7:F6};{8:F3};{9:F3};{10:F3};{11:F3}",
            i, t,
            eyePos.x, eyePos.y, eyePos.z,
            eyeFwd.x, eyeFwd.y, eyeFwd.z,
            angEyeForward, angFace, angChest, angGenital);

        visibleRows.Add(line);
    }

    /// <summary>
    /// Sauvegarde le buffer "as-displayed" live dans un CSV séparé.
    /// </summary>
    public void SaveVisibleCsv()
    {
        try
        {
            System.IO.File.WriteAllLines(visibleCsvPath, visibleRows);
            Debug.Log("Visible-state CSV written to: " + visibleCsvPath);
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to write visible CSV: " + e.Message);
        }
    }
}


/*
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ========================== //
//      CUSTOM EDITOR         //
// ========================== //
#if UNITY_EDITOR
[CustomEditor(typeof(GameManager))]
public class MyScriptEditor : Editor
{
    private string[] fileOptions;
    private int selectedIndexFileData = 0;
    private int selectedIndexFilePlayer = 0;

    void OnEnable()
    {
        LoadFilesFromResources();

        var myScript = (GameManager)target;
        selectedIndexFileData = System.Array.IndexOf(fileOptions, myScript.selectedFileData);
        selectedIndexFilePlayer = System.Array.IndexOf(fileOptions, myScript.selectedFilePlayer);
        if (selectedIndexFileData == -1) selectedIndexFileData = 0;
        if (selectedIndexFilePlayer == -1) selectedIndexFilePlayer = 0;
    }

    public override void OnInspectorGUI()
    {
        var myScript = (GameManager)target;

        EditorGUILayout.LabelField("Eye Tracking Playback", EditorStyles.boldLabel);

        selectedIndexFileData = EditorGUILayout.Popup("Select File Data", selectedIndexFileData, fileOptions);
        selectedIndexFilePlayer = EditorGUILayout.Popup("Select File User", selectedIndexFilePlayer, fileOptions);

        myScript.selectedFileData = fileOptions[selectedIndexFileData];
        myScript.selectedFilePlayer = fileOptions[selectedIndexFilePlayer];

        EditorGUILayout.Space();
        if (GUILayout.Button("Generate Data (as-displayed)", GUILayout.Height(40)))
        {
            myScript.GenerateOutputFile();   // export principal (ce qui est visuellement affiché)
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("As-displayed CSV (live buffer)", EditorStyles.boldLabel);
        myScript.recordVisibleCSV = EditorGUILayout.Toggle("Record Visible CSV", myScript.recordVisibleCSV);
        if (GUILayout.Button("Save Visible CSV Buffer"))
        {
            myScript.SaveVisibleCsv();
        }

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        DrawDefaultInspector();
    }

    private void LoadFilesFromResources()
    {
        fileOptions = Resources.LoadAll<TextAsset>("").Select(a => a.name).ToArray();
        fileOptions = new string[] { "Select a file" }.Concat(fileOptions).ToArray();
    }
}
#endif

// ========================== //
//        GAME MANAGER        //
// ========================== //
public class GameManager : MonoBehaviour
{
    class PositionDataPlayer
    {
        public float time;
        public Vector3 camPose;
        public Vector3 camRotation;                        // Euler monde (Unity)
        public Vector3 combinedEyesGazeOrigin;            // SRanipal: local tête (m)
        public Vector3 combinedEyesGazeDirectionNormalized; // vecteur unitaire SRanipal
        public Vector3 combinedEyesGazeDirection;         // Euler local œil (X=pitch, Y=yaw, Z=0)
    }

    class PositionDataObject
    {
        public float time;
        public Vector3 visagePosition;   // monde
        public Vector3 seinsPosition;    // monde
        public Vector3 genitalPosition;  // monde
    }

    [HideInInspector] [SerializeField] public string selectedFileData = "";
    [HideInInspector] [SerializeField] public string selectedFilePlayer = "";

    private string[] dataPlayer;
    private string[] dataSceneObject;

    private List<PositionDataPlayer> playerPositions;
    private List<PositionDataObject> objectPositions;

    public bool isPlaying = false;
    public int positionPlayback = 0;

    public bool invertAxisCoordinateSystem; // conservé si tu en as besoin

    private GameObject playerHead;
    private GameObject playerEyeGaze;
    private GameObject visageAvatar;
    private GameObject seinsAvatar;
    private GameObject genitalAvatar;

    private GUIStyle customStyle;
    public Vector3 eyeGazeDirectionNormalized;

    // Buffer CSV “as-displayed” live (optionnel)
    [HideInInspector] public bool recordVisibleCSV = false;
    private List<string> visibleRows = new List<string>();
    private string visibleCsvPath;

    void Start()
    {
        playerPositions = new List<PositionDataPlayer>();
        objectPositions = new List<PositionDataObject>();

        playerHead = GameObject.Find("PlayerHead");
        playerEyeGaze = GameObject.Find("PlayerEyeGaze");
        visageAvatar = GameObject.Find("VisageAvatar");
        seinsAvatar = GameObject.Find("SeinsAvatar");
        genitalAvatar = GameObject.Find("GenitalAvatar");

        LoadFile();

        // Init du buffer “as-displayed” live
        visibleCsvPath = System.IO.Path.Combine(Application.persistentDataPath, "visible_export.csv");
        visibleRows.Clear();
        visibleRows.Add("Frame;Time;EyePosX;EyePosY;EyePosZ;EyeFwdX;EyeFwdY;EyeFwdZ;AngEyeForward;AngEyeFace;AngEyeChest;AngEyeGenital");

        Debug.Log("[GameManager] persistentDataPath = " + Application.persistentDataPath);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPlaying = !isPlaying;
            if (isPlaying) StartCoroutine(PlayData());
            else StopAllCoroutines();
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (positionPlayback < playerPositions.Count)
            {
                isPlaying = false;
                positionPlayback++;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            if (positionPlayback > 0)
            {
                isPlaying = false;
                positionPlayback--;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKey(KeyCode.RightArrow))
        {
            if (positionPlayback < playerPositions.Count)
            {
                isPlaying = false;
                positionPlayback++;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            if (positionPlayback > 0)
            {
                isPlaying = false;
                positionPlayback--;
                PlaybackSpecificFrame(positionPlayback);
            }
        }

        // Raccourcis pratiques
        if (Input.GetKeyDown(KeyCode.R))
        {
            recordVisibleCSV = !recordVisibleCSV;
            Debug.Log("Record as-displayed CSV buffer: " + (recordVisibleCSV ? "ON" : "OFF"));
        }
        if (Input.GetKeyDown(KeyCode.S))
        {
            SaveVisibleCsv();
        }
    }

    private void OnGUI()
    {
        if (customStyle == null)
        {
            customStyle = new GUIStyle(GUI.skin.label);
            Texture2D bgTexture = new Texture2D(1, 1);
            bgTexture.SetPixel(0, 0, Color.black);
            bgTexture.Apply();
            customStyle.normal.background = bgTexture;
            customStyle.normal.textColor = Color.white;
            customStyle.fontSize = 22;
            customStyle.padding = new RectOffset(10, 0, 5, 5);
        }

        if (dataPlayer != null && dataPlayer.Length > 0)
        {
            float angleYeuxDevant = Vector3.Angle(playerEyeGaze.transform.forward, Vector3.forward);

            Vector3 positionPlayerEyes = playerEyeGaze.transform.position;
            Vector3 toPositionVisage = (objectPositions[positionPlayback].visagePosition - positionPlayerEyes).normalized;
            Vector3 toPositionSeins = (objectPositions[positionPlayback].seinsPosition - positionPlayerEyes).normalized;
            Vector3 toPositionGenital = (objectPositions[positionPlayback].genitalPosition - positionPlayerEyes).normalized;

            float angleYeuxVisage = Vector3.Angle(playerEyeGaze.transform.forward, toPositionVisage);
            float angleYeuxSeins = Vector3.Angle(playerEyeGaze.transform.forward, toPositionSeins);
            float angleYeuxGenital = Vector3.Angle(playerEyeGaze.transform.forward, toPositionGenital);

            if (playerPositions[positionPlayback].combinedEyesGazeDirectionNormalized == -Vector3.one)
            {
                angleYeuxDevant = 0;
                angleYeuxVisage = 0;
                angleYeuxSeins = 0;
                angleYeuxGenital = 0;
            }

            GUI.Label(new Rect(10, 10, 300, 40), "Frame: " + positionPlayback, customStyle);
            GUI.Label(new Rect(10, 55, 300, 40), $"Angle Yeux-Devant: {angleYeuxDevant:F3} °", customStyle);
            GUI.Label(new Rect(10, 100, 300, 40), $"Angle Yeux-Visage: {angleYeuxVisage:F3} °", customStyle);
            GUI.Label(new Rect(10, 145, 300, 40), $"Angle Yeux-Seins: {angleYeuxSeins:F3} °", customStyle);
            GUI.Label(new Rect(10, 190, 300, 40), $"Angle Yeux-Genital: {angleYeuxGenital:F3} °", customStyle);
        }
    }

    private void PlaybackSpecificFrame(int i)
    {
        Vector3 cameraRotation = playerPositions[i].camRotation;
        Vector3 combinedEyesGazeDirection = playerPositions[i].combinedEyesGazeDirection;

        if (invertAxisCoordinateSystem)
        {
            cameraRotation = new Vector3(cameraRotation.x, cameraRotation.y, cameraRotation.z);
            combinedEyesGazeDirection = new Vector3(-combinedEyesGazeDirection.y, -combinedEyesGazeDirection.x, combinedEyesGazeDirection.z);
        }

        eyeGazeDirectionNormalized = playerPositions[i].combinedEyesGazeDirectionNormalized;

        playerHead.transform.SetPositionAndRotation(playerPositions[i].camPose, Quaternion.Euler(cameraRotation));

        if (playerPositions[i].combinedEyesGazeDirectionNormalized != -Vector3.one)
        {
            // PlayerEyeGaze = enfant de PlayerHead (local tête)
            playerEyeGaze.transform.localPosition = playerPositions[i].combinedEyesGazeOrigin;
            playerEyeGaze.transform.localRotation = Quaternion.Euler(combinedEyesGazeDirection);
        }

        visageAvatar.transform.position = objectPositions[i].visagePosition;
        seinsAvatar.transform.position = objectPositions[i].seinsPosition;
        genitalAvatar.transform.position = objectPositions[i].genitalPosition;

        // Capture live dans le buffer (optionnel)
        CaptureVisibleFrame(i);
    }

    IEnumerator PlayData()
    {
        if (isPlaying)
        {
            for (int i = positionPlayback; i < playerPositions.Count; i++)
            {
                if (!isPlaying) break;
                PlaybackSpecificFrame(i);
                positionPlayback = i;
                yield return new WaitForEndOfFrame();
            }
        }
    }

    // ========================== //
    //          LOADING           //
    // ========================== //
    void LoadFile()
    {
        TextAsset csvDataPlayer = Resources.Load<TextAsset>(selectedFilePlayer);
        TextAsset csvDataObject = Resources.Load<TextAsset>(selectedFileData);

        dataPlayer = csvDataPlayer.text.Replace(",", ".").Split('\n');
        dataSceneObject = csvDataObject.text.Replace(",", ".").Split('\n');

        ParseDataPlayer();
    }

    void ParseDataPlayer()
    {
        // Player
        for (int i = 1; i < dataPlayer.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(dataPlayer[i])) continue;

            string[] row = dataPlayer[i].Split(new char[] { ';' });
            if (row.Length < 37) continue;

            var temp = new PositionDataPlayer();

            temp.time = float.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture);
            temp.camPose = new Vector3(
                float.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture));

            // camRotation: 3 composantes quaternion (x,y,z) → recompose w → Euler
            temp.camRotation = new Vector3(
                float.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture));
            float x = temp.camRotation.x;
            float y = temp.camRotation.y;
            float z = temp.camRotation.z;
            float w = Mathf.Sqrt(Mathf.Max(0f, 1 - (x * x + y * y + z * z)));
            Quaternion q = new Quaternion(x, y, z, w);
            temp.camRotation = q.eulerAngles;

            temp.combinedEyesGazeOrigin = new Vector3(
                float.Parse(row[34], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[35], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[36], System.Globalization.CultureInfo.InvariantCulture));
            temp.combinedEyesGazeOrigin /= 1000f; // mm → m

            temp.combinedEyesGazeDirectionNormalized = new Vector3(
                float.Parse(row[31], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[32], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[33], System.Globalization.CultureInfo.InvariantCulture));

            Vector3 g = temp.combinedEyesGazeDirectionNormalized;
            float yaw = Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(g.y, g.z) * Mathf.Rad2Deg;
            temp.combinedEyesGazeDirection = new Vector3(pitch, yaw, 0f); // Unity: X=pitch, Y=yaw

            playerPositions.Add(temp);
        }

        // Objects (ROIs monde)
        for (int i = 1; i < dataSceneObject.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(dataSceneObject[i])) continue;
            string[] row = dataSceneObject[i].Split(new char[] { ';' });
            if (row.Length < 12) continue;

            var tempObject = new PositionDataObject();
            tempObject.visagePosition = new Vector3(
                float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture));
            tempObject.seinsPosition = new Vector3(
                float.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[7], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[8], System.Globalization.CultureInfo.InvariantCulture));
            tempObject.genitalPosition = new Vector3(
                float.Parse(row[9], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[10], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[11], System.Globalization.CultureInfo.InvariantCulture));

            objectPositions.Add(tempObject);
        }
    }

    // ========================== //
    //        EXPORT (CSV)        //
    // ========================== //

    /// <summary>
    /// Export PRINCIPAL: écrit exactement ce qui est AFFICHÉ (as-displayed).
    /// Évite tout recalcul → pas de mismatch.
    /// </summary>
    public void GenerateOutputFile()
    {
        if (playerPositions == null || playerPositions.Count == 0)
        {
            Debug.LogWarning("No player data loaded.");
            return;
        }

        string outputFilePath = System.IO.Path.Combine(
            Application.persistentDataPath,
            $"{selectedFilePlayer}_{selectedFileData}_output.csv"
        );

        // En-tête
        System.IO.File.WriteAllText(outputFilePath, "Frame;Time;AngleYeuxDevant;AngleYeuxVisage;AngleYeuxSeins;AngleYeuxGenital\n");

        var lines = new System.Text.StringBuilder(4096);

        // IMPORTANT : on positionne la scène sur chaque frame, puis on lit les transforms
        for (int i = 0; i < playerPositions.Count; i++)
        {
            PlaybackSpecificFrame(i); // met à jour la scène (tête, yeux, ROIs)

            // Lire CE QUI EST A L'ÉCRAN
            Vector3 eyePos = playerEyeGaze.transform.position;
            Vector3 eyeFwd = playerEyeGaze.transform.forward.normalized;

            Vector3 pFace = visageAvatar.transform.position;
            Vector3 pChest = seinsAvatar.transform.position;
            Vector3 pGen = genitalAvatar.transform.position;

            float angleYeuxDevant = Vector3.Angle(eyeFwd, Vector3.forward);
            float angleYeuxVisage = Vector3.Angle(eyeFwd, (pFace - eyePos).normalized);
            float angleYeuxSeins = Vector3.Angle(eyeFwd, (pChest - eyePos).normalized);
            float angleYeuxGenital = Vector3.Angle(eyeFwd, (pGen - eyePos).normalized);

            if (playerPositions[i].combinedEyesGazeDirectionNormalized == -Vector3.one)
            {
                angleYeuxDevant = angleYeuxVisage = angleYeuxSeins = angleYeuxGenital = -1f;
            }

            lines.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "{0};{1:F6};{2:F3};{3:F3};{4:F3};{5:F3}\n",
                i, playerPositions[i].time,
                angleYeuxDevant, angleYeuxVisage, angleYeuxSeins, angleYeuxGenital);
        }

        // Remplacer virgules fr si jamais, puis écrire
        string output = lines.ToString().Replace(',', ';');
        System.IO.File.AppendAllText(outputFilePath, output);

        Debug.Log("Output (as-displayed) CSV generated at: " + outputFilePath);
    }

    /// <summary>
    /// Capture la frame visible dans un buffer (optionnel, pour debug ou log live).
    /// </summary>
    private void CaptureVisibleFrame(int i)
    {
        if (!recordVisibleCSV) return;
        if (i < 0 || i >= playerPositions.Count) return;

        Vector3 eyePos = playerEyeGaze.transform.position;
        Vector3 eyeFwd = playerEyeGaze.transform.forward.normalized;

        Vector3 pFace = visageAvatar.transform.position;
        Vector3 pChest = seinsAvatar.transform.position;
        Vector3 pGen = genitalAvatar.transform.position;

        float angEyeForward = Vector3.Angle(eyeFwd, Vector3.forward);
        float angFace = Vector3.Angle(eyeFwd, (pFace - eyePos).normalized);
        float angChest = Vector3.Angle(eyeFwd, (pChest - eyePos).normalized);
        float angGenital = Vector3.Angle(eyeFwd, (pGen - eyePos).normalized);

        float t = playerPositions[i].time;

        string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0};{1:F6};{2:F6};{3:F6};{4:F6};{5:F6};{6:F6};{7:F6};{8:F3};{9:F3};{10:F3};{11:F3}",
            i, t,
            eyePos.x, eyePos.y, eyePos.z,
            eyeFwd.x, eyeFwd.y, eyeFwd.z,
            angEyeForward, angFace, angChest, angGenital);

        visibleRows.Add(line);
    }

    /// <summary>
    /// Sauvegarde le buffer "as-displayed" live dans un CSV séparé.
    /// </summary>
    public void SaveVisibleCsv()
    {
        try
        {
            System.IO.File.WriteAllLines(visibleCsvPath, visibleRows);
            Debug.Log("Visible-state CSV written to: " + visibleCsvPath);
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to write visible CSV: " + e.Message);
        }
    }
}
*/


/*
using System.Collections;
using System.Collections.Generic;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

#if UNITY_EDITOR
[CustomEditor(typeof(GameManager))]
public class MyScriptEditor : Editor
{
    private string[] fileOptions;
    private int selectedIndexFileData = 0;
    private int selectedIndexFilePlayer = 0;

    private void OnEnable()
    {
        LoadFilesFromResources();

        var myScript = target as GameManager;
        selectedIndexFileData = System.Array.IndexOf(fileOptions, myScript.selectedFileData);
        if (selectedIndexFileData == -1) selectedIndexFileData = 0;

        selectedIndexFilePlayer = System.Array.IndexOf(fileOptions, myScript.selectedFilePlayer);
        if (selectedIndexFilePlayer == -1) selectedIndexFilePlayer = 0;
    }

    public override void OnInspectorGUI()
    {
        var myScript = target as GameManager;

        EditorGUILayout.LabelField("Eye Tracking Playback", EditorStyles.boldLabel);

        selectedIndexFileData = EditorGUILayout.Popup("Select File Data", selectedIndexFileData, fileOptions);
        selectedIndexFilePlayer = EditorGUILayout.Popup("Select File User", selectedIndexFilePlayer, fileOptions);

        myScript.selectedFileData = fileOptions[selectedIndexFileData];
        myScript.selectedFilePlayer = fileOptions[selectedIndexFilePlayer];

        if (GUILayout.Button("Generate Data", GUILayout.Height(40)))
        {
            myScript.GenerateOutputFile();
        }

        // --- NEW: contrôles pour le CSV "as-displayed"
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("As-displayed CSV", EditorStyles.boldLabel);
        myScript.recordVisibleCSV = EditorGUILayout.Toggle("Record Visible CSV", myScript.recordVisibleCSV);
        if (GUILayout.Button("Save Visible CSV"))
        {
            myScript.SaveVisibleCsv();
        }

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        DrawDefaultInspector();
    }

    private void LoadFilesFromResources()
    {
        fileOptions = Resources.LoadAll<TextAsset>("")
                               .Select(asset => asset.name)
                               .ToArray();
        fileOptions = new string[] { "Select a file" }.Concat(fileOptions).ToArray();
    }
}
#endif

public class GameManager : MonoBehaviour
{
    class PositionDataPlayer
    {
        public float time;
        public Vector3 camPose;
        public Vector3 camRotation;
        public Vector3 combinedEyesGazeOrigin;
        public Vector3 combinedEyesGazeDirectionNormalized;
        public Vector3 combinedEyesGazeDirection; // Euler (X=pitch, Y=yaw, Z=roll=0)
    }

    class PositionDataObject
    {
        public float time;
        public Vector3 visagePosition;
        public Vector3 seinsPosition;
        public Vector3 genitalPosition;
    }

    [HideInInspector] [SerializeField] public string selectedFileData = "";
    [HideInInspector] [SerializeField] public string selectedFilePlayer = "";

    private string[] dataPlayer;
    private string[] dataSceneObject;

    private List<PositionDataPlayer> playerPositions;
    private List<PositionDataObject> objectPositions;

    public bool isPlaying = false;
    public int positionPlayback = 0;

    public bool invertAxisCoordinateSystem;

    private GameObject playerHead;
    private GameObject playerEyeGaze;
    private GameObject visageAvatar;
    private GameObject seinsAvatar;
    private GameObject genitalAvatar;

    private GUIStyle customStyle;
    public Vector3 eyeGazeDirectionNormalized;

    // === NEW: enregistrement CSV "as-displayed"
    [HideInInspector] public bool recordVisibleCSV = false;         // toggle
    private List<string> visibleRows = new List<string>();          // buffer
    private string visibleCsvPath;                                  // path

    void Start()
    {
        playerPositions = new List<PositionDataPlayer>();
        objectPositions = new List<PositionDataObject>();

        playerHead = GameObject.Find("PlayerHead");
        playerEyeGaze = GameObject.Find("PlayerEyeGaze");
        visageAvatar = GameObject.Find("VisageAvatar");
        seinsAvatar = GameObject.Find("SeinsAvatar");
        genitalAvatar = GameObject.Find("GenitalAvatar");

        LoadFile();

        // === NEW: init CSV "as-displayed"
        visibleCsvPath = System.IO.Path.Combine(Application.persistentDataPath, "visible_export.csv");
        visibleRows.Clear();
        visibleRows.Add("Frame;Time;EyePosX;EyePosY;EyePosZ;EyeFwdX;EyeFwdY;EyeFwdZ;AngEyeForward;AngEyeFace;AngEyeChest;AngEyeGenital");
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPlaying = !isPlaying;

            if (isPlaying) StartCoroutine(PlayData());
            else StopAllCoroutines(); // simple & sûr
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (positionPlayback < playerPositions.Count)
            {
                isPlaying = false;
                positionPlayback++;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            if (positionPlayback > 0)
            {
                isPlaying = false;
                positionPlayback--;
                PlaybackSpecificFrame(positionPlayback);
            }
        }

        if (Input.GetKey(KeyCode.RightArrow))
        {
            if (positionPlayback < playerPositions.Count)
            {
                isPlaying = false;
                positionPlayback++;
                PlaybackSpecificFrame(positionPlayback);
            }
        }
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            if (positionPlayback > 0)
            {
                isPlaying = false;
                positionPlayback--;
                PlaybackSpecificFrame(positionPlayback);
            }
        }

        // === NEW: raccourcis pratiques
        if (Input.GetKeyDown(KeyCode.R))
        {
            recordVisibleCSV = !recordVisibleCSV;
            Debug.Log("Record as-displayed CSV: " + (recordVisibleCSV ? "ON" : "OFF"));
        }
        if (Input.GetKeyDown(KeyCode.S))
        {
            SaveVisibleCsv();
        }
    }

    private void OnGUI()
    {
        if (customStyle == null)
        {
            customStyle = new GUIStyle(GUI.skin.label);
            Texture2D bgTexture = new Texture2D(1, 1);
            bgTexture.SetPixel(0, 0, Color.black);
            bgTexture.Apply();
            customStyle.normal.background = bgTexture;
            customStyle.normal.textColor = Color.white;
            customStyle.fontSize = 22;
            customStyle.padding = new RectOffset(10, 0, 5, 5);
        }

        if (dataPlayer != null && dataPlayer.Length > 0)
        {
            float angleYeuxDevant = Vector3.Angle(playerEyeGaze.transform.forward, Vector3.forward);

            Vector3 positionPlayerEyes = playerEyeGaze.transform.position;

            Vector3 toPositionVisage = (objectPositions[positionPlayback].visagePosition - positionPlayerEyes).normalized;
            Vector3 toPositionSeins = (objectPositions[positionPlayback].seinsPosition - positionPlayerEyes).normalized;
            Vector3 toPositionGenital = (objectPositions[positionPlayback].genitalPosition - positionPlayerEyes).normalized;

            float angleYeuxVisage = Vector3.Angle(playerEyeGaze.transform.forward, toPositionVisage);
            float angleYeuxSeins = Vector3.Angle(playerEyeGaze.transform.forward, toPositionSeins);
            float angleYeuxGenital = Vector3.Angle(playerEyeGaze.transform.forward, toPositionGenital);

            if (playerPositions[positionPlayback].combinedEyesGazeDirectionNormalized == -Vector3.one)
            {
                angleYeuxDevant = 0;
                angleYeuxVisage = 0;
                angleYeuxSeins = 0;
                angleYeuxGenital = 0;
            }

            GUI.Label(new Rect(10, 10, 300, 40), "Frame: " + positionPlayback, customStyle);
            GUI.Label(new Rect(10, 55, 300, 40), $"Angle Yeux-Devant: {angleYeuxDevant:F3} °", customStyle);
            GUI.Label(new Rect(10, 100, 300, 40), $"Angle Yeux-Visage: {angleYeuxVisage:F3} °", customStyle);
            GUI.Label(new Rect(10, 145, 300, 40), $"Angle Yeux-Seins: {angleYeuxSeins:F3} °", customStyle);
            GUI.Label(new Rect(10, 190, 300, 40), $"Angle Yeux-Genital: {angleYeuxGenital:F3} °", customStyle);
        }
    }

    private void PlaybackSpecificFrame(int i)
    {
        Vector3 cameraRotation = playerPositions[i].camRotation;
        Vector3 combinedEyesGazeDirection = playerPositions[i].combinedEyesGazeDirection;

        if (invertAxisCoordinateSystem)
        {
            cameraRotation = new Vector3(cameraRotation.x, cameraRotation.y, cameraRotation.z);
            combinedEyesGazeDirection = new Vector3(-combinedEyesGazeDirection.y, -combinedEyesGazeDirection.x, combinedEyesGazeDirection.z);
        }

        eyeGazeDirectionNormalized = playerPositions[i].combinedEyesGazeDirectionNormalized;

        playerHead.transform.SetPositionAndRotation(playerPositions[i].camPose, Quaternion.Euler(cameraRotation));

        if (playerPositions[i].combinedEyesGazeDirectionNormalized != -Vector3.one)
        {
            // PlayerEyeGaze doit être enfant de PlayerHead (local = tête)
            playerEyeGaze.transform.localPosition = playerPositions[i].combinedEyesGazeOrigin;
            playerEyeGaze.transform.localRotation = Quaternion.Euler(combinedEyesGazeDirection);
        }

        visageAvatar.transform.position = objectPositions[i].visagePosition;
        seinsAvatar.transform.position = objectPositions[i].seinsPosition;
        genitalAvatar.transform.position = objectPositions[i].genitalPosition;

        // === NEW: capture "as-displayed" pour cette frame
        CaptureVisibleFrame(i);
    }

    IEnumerator PlayData()
    {
        if (isPlaying)
        {
            for (int i = positionPlayback; i < playerPositions.Count; i++)
            {
                if (!isPlaying) break;
                PlaybackSpecificFrame(i);
                positionPlayback = i;
                yield return new WaitForEndOfFrame();
            }
        }
    }

    void LoadFile()
    {
        TextAsset csvDataPlayer = Resources.Load<TextAsset>(selectedFilePlayer);
        TextAsset csvDataObject = Resources.Load<TextAsset>(selectedFileData);

        dataPlayer = csvDataPlayer.text.Replace(",", ".").Split('\n');
        dataSceneObject = csvDataObject.text.Replace(",", ".").Split('\n');

        ParseDataPlayer();
    }

    void ParseDataPlayer()
    {
        // Player
        for (int i = 1; i < dataPlayer.Length; i++)
        {
            if (string.IsNullOrEmpty(dataPlayer[i])) continue;

            string[] row = dataPlayer[i].Split(new char[] { ';' });
            PositionDataPlayer temp = new PositionDataPlayer();

            temp.time = float.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture);
            temp.camPose = new Vector3(
                float.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture));

            // camRotation: 3 premiers compos du quaternion (x,y,z) → on recompose w, puis Euler
            temp.camRotation = new Vector3(
                float.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture));

            float x = temp.camRotation.x;
            float y = temp.camRotation.y;
            float z = temp.camRotation.z;
            float w = Mathf.Sqrt(Mathf.Max(0f, 1 - (x * x + y * y + z * z)));
            Quaternion quaternion = new Quaternion(x, y, z, w);
            Vector3 eulerAngles = quaternion.eulerAngles;
            temp.camRotation = eulerAngles;

            temp.combinedEyesGazeOrigin = new Vector3(
                float.Parse(row[34], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[35], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[36], System.Globalization.CultureInfo.InvariantCulture));
            temp.combinedEyesGazeOrigin /= 1000f; // mm → m

            temp.combinedEyesGazeDirectionNormalized = new Vector3(
                float.Parse(row[31], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[32], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[33], System.Globalization.CultureInfo.InvariantCulture));

            Vector3 g = temp.combinedEyesGazeDirectionNormalized;

            // --- CHANGED: Unity Euler (X=pitch, Y=yaw, Z=roll)
            float yaw = Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(g.y, g.z) * Mathf.Rad2Deg;
            temp.combinedEyesGazeDirection = new Vector3(pitch, yaw, 0f);

            playerPositions.Add(temp);
        }

        // Objects / ROIs
        for (int i = 1; i < dataSceneObject.Length; i++)
        {
            if (string.IsNullOrEmpty(dataSceneObject[i])) continue;

            string[] row = dataSceneObject[i].Split(new char[] { ';' });
            PositionDataObject tempObject = new PositionDataObject();

            tempObject.visagePosition = new Vector3(
                float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[5], System.Globalization.CultureInfo.InvariantCulture));
            tempObject.seinsPosition = new Vector3(
                float.Parse(row[6], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[7], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[8], System.Globalization.CultureInfo.InvariantCulture));
            tempObject.genitalPosition = new Vector3(
                float.Parse(row[9], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[10], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(row[11], System.Globalization.CultureInfo.InvariantCulture));

            objectPositions.Add(tempObject);
        }
    }

    public void GenerateOutputFile()
    {
        // Création dossier Output (éditeur uniquement)
#if UNITY_EDITOR
        string folderPath = Application.dataPath + "/Output";
        if (!AssetDatabase.IsValidFolder("Assets/Output"))
        {
            AssetDatabase.CreateFolder("Assets", "Output");
        }
#endif

        playerPositions = new List<PositionDataPlayer>();
        objectPositions = new List<PositionDataObject>();
        LoadFile();

        string outputFilePath =
#if UNITY_EDITOR
            (Application.dataPath + "/Output/" + selectedFilePlayer + "_" + selectedFileData + "_output.csv");
#else
            System.IO.Path.Combine(Application.persistentDataPath, selectedFilePlayer + "_" + selectedFileData + "_output.csv");
#endif

        System.IO.File.WriteAllText(outputFilePath, "Frame;Time;AngleYeuxDevant;AngleYeuxVisage;AngleYeuxSeins;AngleYeuxGenital\n");

        string outputLine = "";

        for (int i = 0; i < playerPositions.Count; i++)
        {
            Vector3 cameraRotation = playerPositions[i].camRotation;
            Vector3 combinedEyesGazeDirection = playerPositions[i].combinedEyesGazeDirection;

            if (invertAxisCoordinateSystem)
            {
                cameraRotation = new Vector3(cameraRotation.x, cameraRotation.y, cameraRotation.z);
                combinedEyesGazeDirection = new Vector3(-combinedEyesGazeDirection.y, -combinedEyesGazeDirection.x, combinedEyesGazeDirection.z);
            }

            Quaternion head = Quaternion.Euler(cameraRotation);
            Quaternion gaze = Quaternion.Euler(combinedEyesGazeDirection);
            Quaternion combinedGaze = head * gaze;

            // ✅ position monde des yeux = camPose + head * offset_local
            Vector3 positionPlayerEyes = playerPositions[i].camPose + head * playerPositions[i].combinedEyesGazeOrigin;

            Vector3 toPositionVisage = (objectPositions[i].visagePosition - positionPlayerEyes).normalized;
            Vector3 toPositionSeins = (objectPositions[i].seinsPosition - positionPlayerEyes).normalized;
            Vector3 toPositionGenital = (objectPositions[i].genitalPosition - positionPlayerEyes).normalized;

            float angleYeuxDevant = Vector3.Angle(combinedGaze * Vector3.forward, Vector3.forward);
            float angleYeuxVisage = Vector3.Angle(combinedGaze * Vector3.forward, toPositionVisage);
            float angleYeuxSeins = Vector3.Angle(combinedGaze * Vector3.forward, toPositionSeins);
            float angleYeuxGenital = Vector3.Angle(combinedGaze * Vector3.forward, toPositionGenital);

            outputLine += $"{i};{playerPositions[i].time};{angleYeuxDevant:F3};{angleYeuxVisage:F3};{angleYeuxSeins:F3};{angleYeuxGenital:F3}\n";

            // (Débug optionnel) comparaison visuel vs export:
            // Vector3 posEyesWorld_Expected = playerHead.transform.position + playerHead.transform.rotation * playerPositions[i].combinedEyesGazeOrigin;
            // Vector3 posEyesWorld_Export   = playerPositions[i].camPose + Quaternion.Euler(cameraRotation) * playerPositions[i].combinedEyesGazeOrigin;
            // Debug.Assert(Vector3.Distance(posEyesWorld_Expected, posEyesWorld_Export) < 1e-3f, "Eye world pos mismatch");
        }

        outputLine = outputLine.Replace(',', ';');
        System.IO.File.AppendAllText(outputFilePath, outputLine);

        Debug.Log("Output file generated at: " + outputFilePath);
    }

    // === NEW: capture de l’état affiché (transforms)
    private void CaptureVisibleFrame(int i)
    {
        if (!recordVisibleCSV) return;
        if (i < 0 || i >= playerPositions.Count) return;

        Vector3 eyePos = playerEyeGaze.transform.position;
        Vector3 eyeFwd = playerEyeGaze.transform.forward.normalized;

        Vector3 pFace = visageAvatar.transform.position;
        Vector3 pChest = seinsAvatar.transform.position;
        Vector3 pGen = genitalAvatar.transform.position;

        float angEyeForward = Vector3.Angle(eyeFwd, Vector3.forward);
        float angFace = Vector3.Angle(eyeFwd, (pFace - eyePos).normalized);
        float angChest = Vector3.Angle(eyeFwd, (pChest - eyePos).normalized);
        float angGenital = Vector3.Angle(eyeFwd, (pGen - eyePos).normalized);

        float t = playerPositions[i].time;

        string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0};{1:F6};{2:F6};{3:F6};{4:F6};{5:F6};{6:F6};{7:F6};{8:F3};{9:F3};{10:F3};{11:F3}",
            i, t,
            eyePos.x, eyePos.y, eyePos.z,
            eyeFwd.x, eyeFwd.y, eyeFwd.z,
            angEyeForward, angFace, angChest, angGenital);

        visibleRows.Add(line);
    }

    // === NEW: sauvegarde du CSV "as-displayed"
    public void SaveVisibleCsv()
    {
        try
        {
            System.IO.File.WriteAllLines(visibleCsvPath, visibleRows);
            Debug.Log("Visible-state CSV written to: " + visibleCsvPath);
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to write visible CSV: " + e.Message);
        }
    }
}
*/