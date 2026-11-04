// Slice files carry no using directives — this file holds the shared set.
// The sibling slice skill and database setup EXTEND this list beneath the '// FILLED BY' marker below.
global using System.Text.Json;          // JsonSerializer, WireJson.Options
global using System.Text.Json.Serialization;  // JsonIgnore on optional event fields
global using Microsoft.AspNetCore.Mvc;
global using Api.Common;             // HttpClients, WireJson
global using Api.Common.Ports;       // IClock
global using Api.Common.Problems;    // ProblemResult, ProblemTypes (catalog projection)
global using Api.Common.Behaviors;   // UnhandledExceptionHandler, ResponseMetadataMiddleware, ResponseHeadersTransformer
// FILLED BY the sibling slice skill — and by skills-db-postgres (copy-assets.ps1) at database setup:
global using Amazon.DynamoDBv2;         // IAmazonDynamoDB — the seat store client
global using Amazon.DynamoDBv2.Model;   // QueryRequest, AttributeValue
global using FluentValidation;
global using FluentValidation.Results;  // ValidationResult in the controller
global using Microsoft.AspNetCore.Authorization;  // [AllowAnonymous] / [Authorize] on slice endpoints
global using Api.Domain;                // Deck, SeatStatus, BlockReason
global using Amazon.SQS;                // IAmazonSQS — the hold timer's queue
global using Amazon.SQS.Model;          // SendMessageRequest
global using Amazon.SimpleNotificationService;  // IAmazonSimpleNotificationService — where seat events go
global using Api.Common.Messaging;      // EventQueueService, ReceivedEvent — the event-queue workers
