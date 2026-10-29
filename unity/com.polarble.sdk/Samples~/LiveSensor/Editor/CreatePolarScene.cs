using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class CreatePolarScene
{
    [MenuItem("Polar/Create Native Sensor Sample")]
    public static void Create()
    {
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var camera=new GameObject("Camera").AddComponent<Camera>();
        camera.transform.position=new Vector3(0,0,-10);camera.orthographic=true;camera.orthographicSize=3.4f;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.04f,.07f);
        var sensor=new GameObject("Polar Native ACC").AddComponent<PolarSensorView>();
        sensor.Replay=true;
        var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.name="Accelerometer Object";cube.transform.position=new Vector3(0,1,0);cube.transform.localScale=new Vector3(1.6f,.25f,.8f);
        sensor.ControlledObject=cube.transform;
        var graph=new GameObject("Acceleration Graph").AddComponent<LineRenderer>();
        graph.transform.position=new Vector3(-2.55f,-1.3f,0);graph.useWorldSpace=false;graph.widthMultiplier=.025f;
        graph.sharedMaterial=new Material(Shader.Find("Sprites/Default"));graph.startColor=graph.endColor=new Color(.1f,.9f,.65f);
        sensor.Graph=graph;
        Selection.activeGameObject=sensor.gameObject;EditorSceneManager.MarkSceneDirty(scene);
    }
}
