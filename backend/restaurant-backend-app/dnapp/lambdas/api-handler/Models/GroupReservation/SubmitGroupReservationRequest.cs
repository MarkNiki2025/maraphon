using System.ComponentModel.DataAnnotations;

namespace APIHandler.Models.GroupReservation;

public class SubmitGroupReservationRequest
{
    [Required]
    public required string ReservationId { get; set; }
}
