using LR.Core.Wrapper;

if (!WrapperServer.TryParseArgs(args, out var instanceId, out var stateDir))
{
    Console.Error.WriteLine("Usage: LR.Wrapper --instance-id <guid> --state-dir <path>");
    return 1;
}

return await WrapperServer.RunAsync(instanceId, stateDir);
