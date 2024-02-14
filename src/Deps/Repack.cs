using System;

internal class RepackDropAttribute : Attribute
{
    public RepackDropAttribute()
    {
    }
}

static internal class DropThese
{
    [RepackDrop]
    static public readonly Grpc.Core.Server.ServiceDefinitionCollection? _;
}