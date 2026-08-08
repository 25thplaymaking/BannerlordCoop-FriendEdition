using GameInterface.AutoSync.Templates;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace GameInterface.Tests.AutoSync;
public class TemplateRenderTests
{
    private readonly ITestOutputHelper output;

    public TemplateRenderTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact(Skip = "Need regeneration")]
    public void PropertySetPrefixTest()
    {
        var result = TemplateParser.Parse("Patches.PropertySetPrefixTemplate",
            new 
            {
                MemberDeclaringType = "TestType",
                MemberName = "TestProperty",
                MemberType = "int"
            });
        SnapshotAssert.Equals(result);
    }

    [Fact]
    public void PropertySetPrefix_SkipsNoOpBeforeClientErrorLog()
    {
        var result = TemplateParser.Parse("Patches.PropertySetPrefixTemplate",
            new
            {
                MemberDeclaringType = "TestType",
                MemberName = "TestProperty",
                MemberType = "int"
            });

        var noOpGuard = result.IndexOf(
            "EqualityComparer<int>.Default.Equals(__instance.TestProperty, value)",
            System.StringComparison.Ordinal);
        var clientLogBranch = result.IndexOf("if (ModInformation.IsClient)", System.StringComparison.Ordinal);

        Assert.True(noOpGuard >= 0, "The generated prefix must contain its equality guard.");
        Assert.True(clientLogBranch >= 0, "The generated prefix must contain its client log branch.");
        Assert.True(noOpGuard < clientLogBranch, "No-op writes must return before the client error log branch.");
    }


    [Fact(Skip = "Need regeneration")]
    public void AssemblyInfoTest()
    {
        var result = TemplateParser.Parse("DynamicAssemblyInfoTemplate", new
        {
            Assemblies = new List<string>
            {
                "Assembly1",
                "Assembly2"
            }
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void FieldSetTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.FieldSetTranspilerTemplate", new
        {
            MemberName = "TestField",
            MemberType = "int",
            MessageType = "TestFieldSet"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void FieldListChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.FieldListChangeTranspilerTemplate", new
        {
            MemberName = "TestFieldList",
            MemberType = "float",
            AddMessageType = "AddListFieldMessage",
            RemoveMessageType = "RemoveListFieldMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void PropertyListChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.PropertyListChangeTranspilerTemplate", new
        {
            MemberName = "TestPropertyList",
            MemberType = "float",
            AddMessageType = "AddListPropertyMessage",
            RemoveMessageType = "RemoveListPropertyMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void FieldMBListChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.FieldListChangeTranspilerTemplate", new
        {
            MemberName = "TestFieldMBList",
            MemberType = "double",
            AddMessageType = "AddMBListFieldMessage",
            RemoveMessageType = "RemoveMBListFieldMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void PropertyMBListChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.PropertyListChangeTranspilerTemplate", new
        {
            MemberName = "TestPropertyMBList",
            MemberType = "double",
            AddMessageType = "AddMBListPropertyMessage",
            RemoveMessageType = "RemoveMBListPropertyMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void FieldQueueChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.FieldListChangeTranspilerTemplate", new
        {
            MemberName = "TestFieldQueue",
            MemberType = "long",
            AddMessageType = "AddQueueFieldMessage",
            RemoveMessageType = "RemoveQueueFieldMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void PropertyQueueChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.PropertyListChangeTranspilerTemplate", new
        {
            MemberName = "TestPropertyQueue",
            MemberType = "long",
            AddMessageType = "AddQueuePropertyMessage",
            RemoveMessageType = "RemoveQueuePropertyMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void FieldArrayChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.FieldListChangeTranspilerTemplate", new
        {
            MemberName = "TestFieldArray",
            MemberType = "string",
            ChangeMessageType = "ChangeArrayFieldMessage"
        });
        SnapshotAssert.Equals(result);
    }

    [Fact(Skip = "Need regeneration")]
    public void PropertyArrayChangeTranspilerTest()
    {
        var result = TemplateParser.Parse("Patches.PropertyListChangeTranspilerTemplate", new
        {
            MemberName = "TestPropertyArray",
            MemberType = "string",
            ChangeMessageType = "ChangeArrayFieldMessage"
        });
        SnapshotAssert.Equals(result);
    }

}
