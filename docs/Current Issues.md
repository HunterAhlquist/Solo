## Bugs
- None, yay :) 
## Improvements
- When the board is empty, include an "ADD AN EPIC" button next to the "ADD A TASK" button (in the same outline style as the epic button in the top right)
- Breakout the epic actions dropdown to have the icons spaced along the bottom of the left hand title section. Just the icons, hover shows MudTooltip with the button label.
- Disable the "owner" switch and remove button on the project settings > members page of the last owner of the project.
- Add some spacing and a horizontal divider between "Project settings" and the above boards.
- Dark-mode only, remove the theme toggle, and force dark mode on all browsers.
- Rename "Instance settings" to "Admin settings"
- Hide the labels tab of project settings to read-only users
- Allow read-only users to comment on tasks
- To decide the color of users without profile pictures, use the users reported name. This way color remains consistent on every client.
- Remove the number that appears epics
- Don't show viewing user on the task card, only on the task editor.
- Add a footer to the layout that has "Copyright ©(YEAR) Ahlquist Interactive", make the text small and subtle. Below that, even more subtle and smaller, have "Slopped up with AI because I am lazy."
## Features
- Alias board related entity IDs to have serial numbers.
	- Syntax of a serial number: (Project Alias)-(Board Alias)-(Auto increment number for tasks) 
		- Example: DMD-PW26-7
	- Use these serial numbers in URLs, each respective alias component standing in for the long GUIDs in the route.
	- Show on task cards under the title in smaller font, and replace "Task" in the editor with the serial number
	- These alias settings are changed in the project and board settings for their respective component, and should be a required field when creating.
	- The final serial numbers could be calculated at runtime
	- Maximum 8 alphanumeric (caps only) characters for both boards and projects
	- Aliases are unique to their siblings, so
		- Two projects can have boards with the same board alias
		- Two boards in the same project are unique
		- Two projects are unique