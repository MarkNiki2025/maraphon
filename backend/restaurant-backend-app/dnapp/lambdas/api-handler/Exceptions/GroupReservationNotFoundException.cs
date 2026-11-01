namespace APIHandler.Exceptions;

public class GroupReservationNotFoundException : ValidationException
{
    public GroupReservationNotFoundException() : base("Group reservation was not found") { }
    public GroupReservationNotFoundException(string message) : base(message) { }
}
