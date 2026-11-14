using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcNodeModelTests
{
    private const string Dbc = """
        VERSION ""

        NS_ :
            BA_DEF_
            BA_

        BS_:

        BU_: Engine Gateway Body

        BO_ 256 EngineData: 8 Engine
         SG_ Rpm : 0|16@1+ (0.25,0) [0|16000] "rpm" Gateway,Body
         SG_ Temp : 16|8@1+ (1,-40) [-40|215] "degC" Gateway

        BO_ 2147483905 BodyStatus: 4 Body
         SG_ Door : 0|1@1+ (1,0) [0|1] "" Engine

        BO_ 512 Shared: 2 Gateway
         SG_ X : 0|8@1+ (1,0) [0|255] "" Vector__XXX

        BO_TX_BU_ 512 : Engine,Body;

        CM_ BU_ Engine "Engine ECU";
        BA_DEF_ "BusType" STRING ;
        BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
        BA_DEF_ SG_ "GenSigStartValue" FLOAT 0 100;
        BA_DEF_ BU_ "NodeAddress" HEX 0 255;
        BA_DEF_ BO_ "GenMsgSendType" ENUM "Cyclic","Event","IfActive";
        BA_DEF_DEF_ "BusType" "CAN";
        BA_DEF_DEF_ "GenMsgCycleTime" 0;
        BA_DEF_DEF_ "GenSigStartValue" 0;
        BA_DEF_DEF_ "NodeAddress" 0;
        BA_DEF_DEF_ "GenMsgSendType" "Cyclic";
        BA_ "BusType" "CAN FD";
        BA_ "GenMsgCycleTime" BO_ 256 100;
        BA_ "GenMsgCycleTime" BO_ 512 70000;
        BA_ "GenMsgSendType" BO_ 256 1;
        BA_ "GenSigStartValue" SG_ 256 Rpm 12.5;
        BA_ "NodeAddress" BU_ Engine 16;
        BA_ "Undefined" BU_ Body 1;
        """;

    private static (DbcDatabase Db, DbcNetworkDefinition Net) Load()
    {
        var db = new DbcParser().Parse("t", new StringReader(Dbc));
        return (db, DbcNetworkDefinition.Read(Dbc));
    }

    [Fact]
    public void NodesTransmitAndReceiveIncludingTxBu()
    {
        var (db, net) = Load();
        var view = new DbcNodeView(db, net);
        Assert.Equal(["Engine", "Gateway", "Body"], view.NodeNames);
        var engine = view.Nodes.Single(n => n.Name == "Engine");
        Assert.Equal("Engine ECU", engine.Comment);
        Assert.Equal(["EngineData", "Shared"], engine.Transmits.Select(m => m.Message).ToArray()); // Shared via BO_TX_BU_
        Assert.Equal(["BodyStatus"], engine.Receives.Select(m => m.Message).ToArray());
        var gw = view.Nodes.Single(n => n.Name == "Gateway");
        Assert.Equal(2, gw.Receives.Single().SignalCount);
        Assert.Equal(["Shared"], gw.Transmits.Select(m => m.Message).ToArray());
        var body = view.Nodes.Single(n => n.Name == "Body");
        Assert.Contains(body.Transmits, m => m.Message == "BodyStatus" && m.IsExtended);
        Assert.Contains(body.Transmits, m => m.Message == "Shared");
    }

    [Fact]
    public void MatrixCsvMarksTxAndRx()
    {
        var (db, net) = Load();
        var csv = new DbcNodeView(db, net).ToMatrixCsv();
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Identifier,Format,Message,DLC,Transmitters,Engine,Gateway,Body", lines[0]);
        Assert.Contains(lines, l => l.Contains(",EngineData,8,Engine,Tx,Rx(2),Rx(1)"));
        Assert.Contains(lines, l => l.Contains(",Shared,2,Gateway; Engine; Body,Tx,Tx,Tx"));
    }

    [Fact]
    public void TypedAttributeDefinitionsAndDefaults()
    {
        var (_, net) = Load();
        var cycle = net.AttributeDefinitions.Single(d => d.Name == "GenMsgCycleTime");
        Assert.Equal(DbcAttributeType.Int, cycle.Type);
        Assert.Equal(DbcAttributeScope.Message, cycle.Scope);
        Assert.Equal(65535, cycle.Maximum);
        Assert.Equal("0", cycle.DefaultText);
        var send = net.AttributeDefinitions.Single(d => d.Name == "GenMsgSendType");
        Assert.Equal(["Cyclic", "Event", "IfActive"], send.EnumValues);
        Assert.Equal("Cyclic", send.DefaultText);
        Assert.Equal(DbcAttributeType.Hex, net.AttributeDefinitions.Single(d => d.Name == "NodeAddress").Type);
        Assert.Equal(DbcAttributeScope.Network, net.AttributeDefinitions.Single(d => d.Name == "BusType").Scope);
        Assert.Equal(DbcAttributeType.Float, net.AttributeDefinitions.Single(d => d.Name == "GenSigStartValue").Type);
    }

    [Fact]
    public void AssignmentsResolveTargetsAndFlagProblems()
    {
        var (_, net) = Load();
        var a = net.AttributeAssignments;
        Assert.Contains(a, x => x.Name == "BusType" && x.Scope == DbcAttributeScope.Network && x.DisplayValue == "CAN FD" && x.Problem is null);
        Assert.Contains(a, x => x.Name == "GenMsgCycleTime" && x.Target == "EngineData" && x.Problem is null);
        Assert.Contains(a, x => x.Name == "GenMsgCycleTime" && x.Target == "Shared" && x.Problem!.Contains("outside"));
        Assert.Contains(a, x => x.Name == "GenMsgSendType" && x.DisplayValue == "Event (1)");
        Assert.Contains(a, x => x.Name == "GenSigStartValue" && x.Target == "EngineData.Rpm" && x.Problem is null);
        Assert.Contains(a, x => x.Name == "NodeAddress" && x.Target == "Engine" && x.DisplayValue == "0x10 (16)");
        Assert.Contains(a, x => x.Name == "Undefined" && x.Problem!.Contains("no BA_DEF_"));
    }

    [Fact]
    public void NetworkMatrixAnalyzerSharesReceiverLogic()
    {
        var (db, _) = Load();
        var rx = DbcNodeView.DeclaredReceivers(db.AllMessageDefinitions.First(m => m.Name == "EngineData"));
        Assert.Equal(["Body", "Gateway"], rx);
        Assert.Empty(DbcNodeView.DeclaredReceivers(db.AllMessageDefinitions.First(m => m.Name == "Shared")));
    }
}
