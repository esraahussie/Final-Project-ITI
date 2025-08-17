using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Uber.BLL.ModelVM.Ride;
using Uber.BLL.Services.Abstraction;
using Uber.DAL.Entities;
using Uber.DAL.Enums;
using System;

namespace Uber.PLL.Controllers
{

    public class RideController : Controller
    {
        private readonly IHubContext<RideHub> _hub;
        private readonly IDriverService _driverService;
        private readonly IRideService _rideService;
        private readonly IUserService _userService;

        public RideController(IHubContext<RideHub> hub, IDriverService driverService, IRideService rideService, IUserService userService)
        {
            _hub = hub;
            _driverService = driverService;
            _rideService = rideService;
            _userService = userService;
        }

        [Authorize] 
        [HttpGet]
        public async Task<IActionResult> RequestRide(double StartLat, double StartLng, double EndLat, double EndLng,
            double Distance, double Duration, double Price)
        {
            try
            {
                var nearest = _driverService.GetNearestDriver(StartLat, StartLng);
                if (!nearest.Item1 || nearest.Item3 == null || !nearest.Item3.Any())
                {
                    return View("NoDrivers");
                }

                var chosenDriverId = nearest.Item3.FirstOrDefault();
                if (string.IsNullOrEmpty(chosenDriverId))
                {
                    return View("NoDrivers");
                }

                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized("User not authenticated");
                }

                var (ok, err, ride) = _rideService.CreatePendingRide(userId, chosenDriverId, StartLat, StartLng, EndLat, EndLng, Distance, Duration, Price, "Wallet", false);
                if (!ok || ride == null)
                {
                    return BadRequest(err ?? "Failed to create ride");
                }

                var rideGroup = $"ride-{ride.Id}";

                
                var driverGroup = $"driver-{chosenDriverId}";

                // Get user information for rating display
                var (userErr, user) = _userService.GetByID(userId);
                var userRating = 5.0; // Default to 5 if user not found
                if (userErr != null)
                {
                    Console.WriteLine($"Warning: Could not get user info for rating: {userErr}");
                }
                else if (user != null)
                {
                    userRating = user.Rating();
                    Console.WriteLine($"User rating retrieved: {userRating}");
                }
                
                await _hub.Clients.Group(driverGroup).SendAsync("ReceiveRideRequest", new
                {
                    rideId = ride.Id,
                    rideGroup,
                    startLat = StartLat,
                    startLng = StartLng,
                    endLat = EndLat,
                    endLng = EndLng,
                    userId,
                    distance = Distance,
                    duration = Duration,
                    price = Price,
                    userRating = Math.Round(userRating, 1)
                });

                // 4) Show waiting view (client will connect to hub & join group)
                return View("WaitingForDriver", ride.Id.ToString());
            }
            catch (Exception ex)
            {
                // Log the exception here
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize] 
        [HttpPost]
        public async Task<IActionResult> RequestRide([FromBody] RideRequestModel request)
        {
            try
            {
                // 1) find nearest driver
                var nearest = _driverService.GetNearestDriver(request.StartLat, request.StartLng);
                if (!nearest.Item1 || nearest.Item3 == null || !nearest.Item3.Any())
                {
                    return BadRequest(new { message = "No drivers available in your area" });
                }

                var chosenDriverId = nearest.Item3.FirstOrDefault();
                if (string.IsNullOrEmpty(chosenDriverId))
                {
                    return BadRequest(new { message = "No drivers available in your area" });
                }

                // 2) create pending ride in DB
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new { message = "User not authenticated" });
                }

                var (ok, err, ride) = _rideService.CreatePendingRide(userId, chosenDriverId, request.StartLat, request.StartLng, request.EndLat, request.EndLng, request.Distance, request.Duration, request.Price, request.PaymentMethod);
                if (!ok || ride == null)
                {
                    return BadRequest(new { message = err ?? "Failed to create ride" });
                }

                var rideGroup = $"ride-{ride.Id}";

                // 3) Notify the target driver
                var driverGroup = $"driver-{chosenDriverId}";

                // Get user information for rating display
                var (userErr, user) = _userService.GetByID(userId);
                var userRating = 5.0; // Default to 5 if user not found
                if (userErr != null)
                {
                    Console.WriteLine($"Warning: Could not get user info for rating: {userErr}");
                }
                else if (user != null)
                {
                    userRating = user.Rating();
                    Console.WriteLine($"User rating retrieved: {userRating}");
                }
                
                await _hub.Clients.Group(driverGroup).SendAsync("ReceiveRideRequest", new
                {
                    rideId = ride.Id,
                    rideGroup,
                    startLat = request.StartLat,
                    startLng = request.StartLng,
                    endLat = request.EndLat,
                    endLng = request.EndLng,
                    userId,
                    distance = request.Distance,
                    duration = request.Duration,
                    price = request.Price,
                    userRating = Math.Round(userRating, 1)
                });

                // 4) Return JSON response with ride ID for the Home page
                return Ok(new
                {
                    success = true,
                    message = "Ride request created successfully",
                    rideId = ride.Id,
                    rideGroup = rideGroup
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"An error occurred: {ex.Message}" });
            }
        }

        // These can be called by Hub as well, but keeping REST fallbacks:
        [Authorize]
        [HttpPost]
        public async Task<IActionResult> DriverAccept([FromBody] AcceptRejectRequest request)
        {
            try
            {
                var (ok, err) = _rideService.MarkAccepted(request.id);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to accept ride");
                }

                // Send notification to the ride group
                await _hub.Clients.Group(request.rideGroup).SendAsync("RideAccepted", request.id);

                var result = _rideService.GetByID(request.id);
                var rdriver = _driverService.AddBalance(result.Item2.DriverId, (result.Item2.PaymentMethod == 0) ? (double)result.Item2.Price * 0.8 : (double)result.Item2.Price * -0.2);
                var rUser = _userService.AddBalance(result.Item2.UserId, (result.Item2.PaymentMethod == 0) ? (double)result.Item2.Price * -1 : 0);
                return Ok(new { success = true, message = "Ride accepted successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> DriverReject([FromBody] AcceptRejectRequest request)
        {
            try
            {
                var (ok, err) = _rideService.MarkRejected(request.id);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to reject ride");
                }

                // Get the rejected ride to find next nearest driver
                var (rideErr, ride) = _rideService.GetByID(request.id);
                if (rideErr != null || ride == null)
                {
                    return BadRequest("Ride not found");
                }

                // Find next nearest driver
                var nextNearest = _driverService.GetNearestDriver(ride.StartLat, ride.StartLng);
                if (nextNearest.Item1 && nextNearest.Item3 != null && nextNearest.Item3.Any())
                {
                    // Skip the driver who just rejected
                    var nextDriverId = nextNearest.Item3.FirstOrDefault(d => d != ride.DriverId);

                    if (!string.IsNullOrEmpty(nextDriverId))
                    {
                        // Update ride with new driver
                        var (updateOk, updateErr) = _rideService.AssignNewDriver(request.id, nextDriverId);
                        if (updateOk)
                        {
                            // Get user information for rating display
                            var (userErr, user) = _userService.GetByID(ride.UserId);
                            var userRating = 5.0; // Default to 5 if user not found
                            if (userErr != null)
                            {
                                Console.WriteLine($"Warning: Could not get user info for rating: {userErr}");
                            }
                            else if (user != null)
                            {
                                userRating = user.Rating();
                                Console.WriteLine($"User rating retrieved: {userRating}");
                            }
                            
                            // Notify the new driver
                            await _hub.Clients.Group($"driver-{nextDriverId}").SendAsync("ReceiveRideRequest", new
                            {
                                rideId = ride.Id,
                                rideGroup = request.rideGroup,
                                startLat = ride.StartLat,
                                startLng = ride.StartLng,
                                endLat = ride.EndLat,
                                endLng = ride.EndLng,
                                userId = ride.UserId,
                                distance = ride.Distance,
                                duration = ride.Duration,
                                price = ride.Price,
                                userRating = Math.Round(userRating, 1)
                            });

                            // Notify user that request was sent to another driver
                            await _hub.Clients.Group(request.rideGroup).SendAsync("RideRejected", request.id);
                            return Ok(new { success = true, message = "Ride rejected, sent to next driver" });
                        }
                    }
                }

                // If no more drivers available, notify user
                await _hub.Clients.Group(request.rideGroup).SendAsync("RideRejected", request.id);
                return Ok(new { success = true, message = "Ride rejected, no more drivers available" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Create(Ride ride)
        {
            if (ModelState.IsValid)
            {
                _rideService.Create(ride);
                return RedirectToAction(nameof(Index));
            }
            return View(ride);
        }

        public IActionResult Request()
        {
            return View();
        }

        public IActionResult NoDrivers()
        {
            return View();
        }

        public IActionResult Test()
        {
            return View();
        }

        [Authorize]
        public IActionResult RideDetails(int id)
        {
            try
            {
                var (err, ride) = _rideService.GetByID(id);
                if (err != null || ride == null)
                {
                    return NotFound("Ride not found");
                }

                // Check if the current user is authorized to view this ride
                var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized("User not authenticated");
                }

                // User can view if they are the ride requester or the assigned driver
                if (ride.UserId != currentUserId && ride.DriverId != currentUserId)
                {
                    return Forbid("You are not authorized to view this ride");
                }

                return View(ride);
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize(Roles = "Driver")]
        [HttpPost]
        public async Task<IActionResult> StartRide([FromBody] AcceptRejectRequest request)
        {
            try
            {
                var (ok, err) = _rideService.MarkInProgress(request.id);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to start ride");
                }

                // Notify both user and driver that ride has started
                await _hub.Clients.Group($"ride-{request.id}").SendAsync("RideStarted", request.id);
                return Ok(new { success = true, message = "Ride started successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize(Roles = "Driver")]
        [HttpPost]
        public async Task<IActionResult> CompleteRide([FromBody] AcceptRejectRequest request)
        {
            try
            {
                var (ok, err) = _rideService.MarkCompleted(request.id);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to complete ride");
                }

                // Notify both user and driver that ride has completed
                await _hub.Clients.Group($"ride-{request.id}").SendAsync("RideCompleted", request.id);
                return Ok(new { success = true, message = "Ride completed successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize(Roles = "Driver")]
        [HttpPost]
        public async Task<IActionResult> DriverArrived([FromBody] AcceptRejectRequest request)
        {
            try
            {
                var (ok, err) = _rideService.MarkDriverWaiting(request.id);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to mark driver as arrived");
                }

                // Notify both user and driver that driver has arrived
                await _hub.Clients.Group($"ride-{request.id}").SendAsync("DriverArrived", request.id);
                return Ok(new { success = true, message = "Driver arrived at pickup location" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> AddUserRating([FromBody] RatingRequest request)
        {
            try
            {
                var (ok, err) = _rideService.AddUserRating(request.RideId, request.Rating);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to add user rating");
                }

                // Notify both user and driver that user rating was added
                await _hub.Clients.Group($"ride-{request.RideId}").SendAsync("UserRated", request.RideId, request.Rating);
                return Ok(new { success = true, message = "User rating added successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> AddDriverRating([FromBody] RatingRequest request)
        {
            try
            {
                var (ok, err) = _rideService.AddDriverRating(request.RideId, request.Rating);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to add driver rating");
                }

                // Notify both user and driver that driver rating was added
                await _hub.Clients.Group($"ride-{request.RideId}").SendAsync("DriverRated", request.RideId, request.Rating);
                return Ok(new { success = true, message = "Driver rating added successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> CancelRide([FromBody] AcceptRejectRequest request)
        {
            try
            {
                var (ok, err) = _rideService.Cancel(request.id);
                if (!ok)
                {
                    return BadRequest(err ?? "Failed to cancel ride");
                }

                // Notify both user and driver that ride has been cancelled
                await _hub.Clients.Group($"ride-{request.id}").SendAsync("RideCancelled", request.id);
                
                // Also notify the driver specifically if they exist
                var (rideErr, ride) = _rideService.GetByID(request.id);
                if (rideErr == null && ride != null && !string.IsNullOrEmpty(ride.DriverId))
                {
                    await _hub.Clients.Group($"driver-{ride.DriverId}").SendAsync("RideCancelled", request.id);
                }
                
                return Ok(new { success = true, message = "Ride cancelled successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }


        [Authorize]
        public IActionResult WaitingForDriver(int? id = null)
        {
            try
            {
                // If an ID is provided, pass it to the view
                if (id.HasValue)
                {
                    return View( model: id.ToString());
                }

                // Otherwise, just show the waiting page without specific ride ID
                return View();
            }
            catch (Exception ex)
            {
                return BadRequest($"An error occurred: {ex.Message}");
            }
        }
    }

}
