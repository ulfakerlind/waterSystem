# waterSystem
This project works is designed for a raspberry pi where the external hardware includes a water sensor, a led light and a waterpump

The project is still being developed and currently contains hardcoded variables for the max and min values of the moisture level 
in the soil and how often it should be watered.

that in practice the user will insert the moisture sensor, let the program know that it can save the min value, water the plant,
and let the program know that it can save the max value. After these steps all that remains is to refill an external water tank
and talk to ones plant.

Furthermore this project uses the Clayster.Library.Internet and Clayster.Library.RaspberryPi which can be downloaded here on github
