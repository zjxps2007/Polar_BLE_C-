using Polar.Native;
using Unity.Collections;
using UnityEngine;
public class PolarSensorView : MonoBehaviour
{
    public string BluetoothAddress="24ACAC000000";
    public Transform ControlledObject;
    public LineRenderer Graph;
    public bool Replay=true;
    public ConnectionPhase ConnectionPhase { get; private set; }
    PolarContext context;
    PolarStream stream;
    ulong connectRequest,startRequest;
    readonly Vector3[] points=new Vector3[256];
    void Start()
    {
        context=new PolarContext();context.EventReceived+=OnEvent;
        if(Replay)startRequest=context.Submit(new Command {Operation=Operation.ReplayOpen,Measurement=Measurement.Acc,Flags=257});
        else connectRequest=context.Connect(System.Convert.ToUInt64(BluetoothAddress,16),true,ConnectionFeatures.Default|ConnectionFeatures.Streaming);
        if(Graph) {Graph.positionCount=points.Length;for(int i=0;i<points.Length;i++)points[i].x=i*.02f;}
    }
    void OnEvent(Polar.Native.Event e,NativeArray<byte> payload,int count)
    {
        if(PolarContext.TryGetConnectionState(e,payload,count,out var state))ConnectionPhase=state.Phase;
        if(e.Type==11&&e.Result==0)startRequest=context.Start(e.Device,Measurement.Acc);
        if(e.Type==1&&e.Result==0)
        {
            if(e.Request==startRequest&&e.Stream!=0)stream=context.AttachStream(e.Stream);
            else if(e.Request==connectRequest&&e.Device!=0)startRequest=context.Start(e.Device,Measurement.Acc);
        }
        if(e.Type==3&&stream!=null){stream.Dispose();stream=null;}
        if(e.Result<0)Debug.LogError("Polar result "+e.Result+" ATT "+e.AttError);
    }
    void Update()
    {
        if(stream==null)return;
        for(int i=0;i<stream.Count;i++)
        {
            var sample=stream.Samples[i];if(sample.ChannelCount<3)continue;
            float x=(float)sample.Value(0)/1000,y=(float)sample.Value(1)/1000;
            if(ControlledObject)ControlledObject.localRotation=Quaternion.Euler(y*30,0,-x*30);
            for(int j=0;j<points.Length-1;j++)points[j].y=points[j+1].y;
            points[points.Length-1].y=x;
        }
        if(Graph)Graph.SetPositions(points);
    }
    void OnDestroy(){context?.Dispose();}
}
