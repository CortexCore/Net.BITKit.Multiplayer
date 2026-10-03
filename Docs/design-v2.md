第二版则以ECS为中心


1.ECS同步

我们有实现，但基本上也是一坨稀饭，所以我们在此继续明确设计


我们默认使用IEntitiesService进行注册，注销Entity的行为

Entity有很多Component，现在我们将明确为Entity注入需要同步的Component

Entity,ServiceCollection.AddSingleton<NetFooComponent>();
Entity.ServiceCollection.AddSingleton<INetComponent>(x=>x.GetReqioredService<NetFooCompoent>)

最后我们可以缓存并直接遍历
foreach(var netComponent in Entity.ServiceProvider.GetServices<INetComponent>)
{
    //首先我们对比Component的指纹是否和已缓存的一致
    if(hasNewValue is false)contiune;
    using var messageBag = 接下来我们继续创建messageBag;

    message.Write... //我们把Entity的ID,NetComponent的Id,顺序写入的新值给传输过去,这点或许可以用MessagePack的全量序列化
}

以上为发送部分，同理，接收部分基本上也是一个逻辑
也就是说，只有在entity存在INetworkIdentity的情况下，才进行同步，暂不差异生成和销毁,仅同步双方都有的Entity的Component

如何更新值呢？我们直接调用接口的Rpc,让其在Host层对Component的值进行修改

ECS默认使用不可靠通道


2.远程接口的同步字段同步
目前已经有该功能和设计，我们再次继续介绍

interface　IFoo{
    UniTask<int> Plus(int a,int b);
    int GetValue;
}

Plus在v1已经介绍，GetValue在V1被视为不支持的方法
我们现在为此编写支持功能

int GetValue和Mirror，NetCode等网络模块一样，其实就是[SyncVar]

只不过我们默认远程接口需要全部同步接口中的字段(实例中的非接口字段不必同步)

重点来了，我们需要支持IList,IDictionary,这两个同步字段

interface　IFoo{
    IList<int> MyList;
    IDictioanry<int,int> MyDictioanry;
}

我们用以下方式提供同步

public class _Foo:IFoo
{
    IList<int> MyList;
    IDictioanry<int,int> MyDictioanry;

    public _Foo(){
        MyList = _rpcContext.GetList<int>();
        MyDictioanry = _rpcContext.GetDictionary<int,int>();
    }
}

而_rpcContext.GetList，是我们的自定义封装类,例如

class NetworkList{

    Add(T t)
    {
        //我们有新的增加事件，带序号
    }
    Remove(T t)
    {
        //同样的，在Host进行计算，并发出同步事件
    }

}

他们本质上，就是向Bag中发送NetworkMessageType.SyncVar请求

然后就是targetId,propertyId,AddOrRemoveOrElse,ValueOrKey等