import { ProjectWorkspace } from "./project-workspace";
import { startView } from "./view";
import "./base.css";
import "./styles.css";

startView(({ api, project, request }) => (
  <ProjectWorkspace api={api} project={project} request={request} />
));
