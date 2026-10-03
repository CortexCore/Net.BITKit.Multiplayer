第一步：我们实现RpcContextService

不再使用什么Inovke,InvokeAsync

而是直接写入NetRpcModel

rpcTargetId:uint

rpcMethodId:uint

rpcContext:
写入数量
然后根据Method的参数，一个一个通过MessagePack读写，
每个method复用一个object[]作为传参

第二步
然后我们定义ITransport
其实没啥，就是首发那一套，啥具体类型都不要有
只有OnRecived和Send

第三步
假如方法是
void Fire(int damage)
我们则使用以下写入方式
using var messageBag(或者整一个MessagePack的Writer)

messageBag.write rpcTargetId
messageBag.write rpcMethodId
messageBag.write damage

然后通过ITranport.Send(messageBag)
注意，此处的参数应该是ReadonlyMemory

第四步
我们通过ITranport接受到了对应的包，我们反序列化，得到以上内容
我们发现我们本地并没有rpcTargetId和rpcMethodId等字段，这是应该的
所以我们需要挂起请求，然后向发送端请求RpcMap，也就是id对应string的name,这样我们可以绑定到Type和Method

第五部
我们得到了真正的class(通过Type从DI获取)，并且得到了MehtodName,完成了调用
如果对应方法不是void,我们就把结果返回回去，用同样的方式

以上5步为v1，原本的BITKit.INetProvider均已实现，只是GC爆炸


剩下的就是通过源码生成的方式，生成原生接口，自动write messageBag啥的

然后把反射调用变为真正的deagle之类的无gc高速句柄


v2

v2版本我们将改进GC和调用方式问题
我们仍然需要创建远程接口，这是DI中必不可少的一环，我们也使用远程接口作为Client和Server的通信方式
也就是Client侧没有实现代码，由真实的Server计算并返回

比如，在公共仓库，有个类叫
interface　IFoo{
    UniTask<int> Plus(int a,int b);
    int GetValue;
}

client侧则直接使用该方式
serviceCollection.AddRemoteInterface<IFoo>();

实际上，内部为
serviceCollection.AddRemoteInterface<IFoo>(x=>CreateRemoteInterface<IFoo>)

也就是编制一套
public class _Foo:IFoo
{
    private RpcContext _contxt;
     UniTask<int> Plus(int a,int b)
     {
            using var bag = NetworkMessageBag.Pool();
            bag.Write(NetCommandType.Rpc) // uint或者int类型
            bag.Write(_context.index);// 写入唯一的序号
            bag.Write(_FooIntId); // 这个接口的Hash或者对应的整数，反正不是String
            bag.Write(_FooRpc) // 同样的，对应这个方法名字，对应的整数Id
            
            //最后我们再写入参数
            bag.Write(a);
            bag.Write(b);


            return _context.InvokeAsync(bag) // 隐式转换为ReadonlyMemory<byte>
     }

     int GetValue=>throw new RpcExp("不支持同步类型");
}


这样的动态代码
这样我们就可以无缝访问远程的接口

之前的NetProvider已经实现，只是因为底层的kcp和ENet有链接上的问题，经常遇到错误


在远程，我们有这种方式

OnRpc(uint peerId,ReadOnlyMemory<byte> bytes)
{
    using var reader = new MyNonGcReader(bytes);

    var commandType =  (NetworkCommand)reader.ReadByte();

    if(commandType is not Rpc) return;//这段示例是给Rpc的

    var index = reader.readInt();//或者uint

    var targetId = reader.readInt();//或者uint之类的

    var methodId = reader.ReadInt();

    var target = ServiceResovler(targetId);

    var delegate = ServiceDelegateResovler(target,methodId);

    var respose = Invoke(delegate,reader);

    if(respose.hasValue)
    {
        using var callback = NetworkMessageBag.Pool();


        callback.Write(NetworkMessage.RpcBack)// 或者callback或者respose反正就是返回
        callback.Write(index);
        callback.write(1) // 0就是无值
        callback.write(respose.Value) // 写回返回值

        transport.SendTo(peedIr,callback) //隐式转为ReadonlyMemory<byte>
    }
}

这就是v2，一来一回


v3这是我们最开始做的内容


RpcContext rpcContext;


[Rpc(SendTo.Host)]
UniTask Fire(int damage){
    //默认的开火逻辑
}

//而通过IL Wrapper后，我们得到以下逻辑(实际调用会命中一下内容)

[Rpc(SendTo.Host)]
async UniTask _Fire(int damage)
{
    if(rpcContext.IsServer)
    {
        _FireInternal(damage);
    }
    else
    {
    using var bag = NetworkMessageBag.Pool();

    bag.write<uint>(rpcName);
    bag.write<uint>

        bag.write<int>(damage);

        await rpcContext.InvokeRpc(myRpcHash,rpcNameHash,argPack);
    }
}

UniTask _FireInternal(int damage)
{
    //这就是原始的开火逻辑
}

然后再Host这边


具体怎么广播就不用我说了吧？
反正就是用NetworkCommandType切换路径，剩下的自己读

这就是v3，区分Host和Client，



有关v4这块，我们之前也做了一部分，但确实是算拉了坨大的，所以我们重新明确设计


//理论上只能Server调用，我们后面也会考虑如何让客户端可以主动调用该Rpc
[Rpc(SendTo.All,RpcDelivery.Unreliable)]
public void UpdatePos(int entityId,float3 pos)
{
    //中这就不一样了哈，相同的写法，但是我们使用不可靠通道，就不能有返回值（不然就要做丢包补发了）
    using var bag = 我们按照默认的方式写好bag

    //是的，我们让ITransport承担可靠和非可靠通道
    //也就是ITransport默认定义了可靠通道(kcp,tcp或者rudp)和不可靠通道(udp,rudp等)
    ITransport.SendFast(bag);
}

而我们的Transport。实际上是个复合通信模块，我们默认不使用组合的方式，来让两个Transport承担握手和通讯业务

而这就是v4，我们新增了不可靠通讯模块，明确要求不能有返回值


有关v5，我们则将网络模块做了一个大升级

主要由ITransport承担，这里我们需要讨论下如何实现“Relay”

理论上就是Tranport格外独立维护一个Client,
Host独立维护一个Client。连接到IRelayEndpoint

Client默认向某个地址发送链接，这个地址可以是Direct Host，也可以是Relay

IRelayEndPoint负责向真正的Host转发请求

最终结构是
Relay
- RelayEndpoint:Server
Host
- Transport:Server
- Transport:Client->Relay.RelayEndPoint
Client
- Transport:Client

Host需要显式连接到Relay,但允许失败，不是必要行为，会尝试重连

Client不分连接到Relay还是Server

到了v5这块，我们基本的网络接口已经完成，剩下的都是gc优化和调用函数的优化