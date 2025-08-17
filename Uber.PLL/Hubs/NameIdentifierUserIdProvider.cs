﻿using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

public class NameIdentifierUserIdProvider : IUserIdProvider
{
    public string GetUserId(HubConnectionContext connection)
        => connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}
