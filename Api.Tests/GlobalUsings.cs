// Slice test files (linked in from {Project}/Features/**/*.Tests.cs) carry no using directives —
// this file holds the test project's shared set. The sibling slice skill EXTENDS it as its
// test helpers need more (e.g. Testcontainers, AngleSharp).
global using Xunit;
global using Api.Common;             // HttpClients, WireJson
global using System.Net;                      // HttpStatusCode
global using System.Net.Http.Json;            // ReadFromJsonAsync
global using System.Text.Json;                // JsonDocument for raw problem bodies
global using Amazon.DynamoDBv2;               // the seat store, seeded directly by slice tests
global using Amazon.DynamoDBv2.Model;
global using Testcontainers.DynamoDb;         // DynamoDB Local in Docker
global using Api.Common.Ports;       // IClock
global using Api.Domain;             // Deck, SeatStatus, BlockReason
global using System.Net.Http.Headers;          // bearer Authorization header
global using System.Security.Cryptography;     // RSA test signing keys
global using Microsoft.AspNetCore.Authentication.JwtBearer;
global using Microsoft.IdentityModel.JsonWebTokens;          // minting test access tokens
global using Microsoft.IdentityModel.Protocols;
global using Microsoft.IdentityModel.Protocols.OpenIdConnect;
global using Microsoft.IdentityModel.Tokens;
global using Api.Tests;              // ApiFactory
global using Amazon.SQS;                       // the hold-expiry queue, in LocalStack
global using Amazon.SQS.Model;
global using Testcontainers.LocalStack;        // LocalStack in Docker
global using Microsoft.Extensions.Configuration;   // in-memory settings for the background workers' handlers
global using Api.Common.Messaging;             // ReceivedEvent, EventOutcome — the event-queue workers
